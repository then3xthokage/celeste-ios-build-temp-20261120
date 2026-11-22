using System;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;

namespace celestemeow
{
    /// <summary>
    /// Produces an identifier that remains stable when Cecil rewrites metadata tokens.
    /// The formatter is shared with the build-time weaver so both sides address the
    /// same original method without relying on token numbers.
    /// </summary>
    internal static class IOSHookIdentity
    {
        internal const string GenericGateStateTypeFullName = "CelesteIOS.Generated.__CelesteIOSGenericGateState";

        internal static string ForMethod(MethodBase method)
        {
            if (method == null) throw new ArgumentNullException(nameof(method));
            MethodBase definition = GetDefinition(method);
            Type declaringType = definition.DeclaringType;
            if (declaringType == null) throw new NotSupportedException("A hook target must have a declaring type: " + method);
            string signature = definition.Module.Assembly.GetName().Name + "|" +
                TypeName(declaringType) + "|" + (definition.IsStatic ? "static" : "instance") + "|" +
                definition.Name + "|" + GenericArity(definition) + "|" +
                ReturnType(definition) + "(" + string.Join(",", definition.GetParameters().Select(SignatureTypeName)) + ")";
            return Hash(signature);
        }

        internal static string ForMethod(MethodDefinition method)
        {
            if (method == null) throw new ArgumentNullException(nameof(method));
            string signature = method.Module.Assembly.Name.Name + "|" +
                TypeName(method.DeclaringType) + "|" + (method.IsStatic ? "static" : "instance") + "|" +
                method.Name + "|" + method.GenericParameters.Count + "|" +
                TypeName(method.ReturnType) + "(" + string.Join(",", method.Parameters.Select(parameter => TypeName(parameter.ParameterType))) + ")";
            return Hash(signature);
        }

        internal static string GateFieldName(MethodBase method) => "__ios_hooked_" + ForMethod(method);
        internal static string OriginalMethodName(MethodBase method) => "__ios_original_" + ForMethod(method);
        internal static string ValueTypeInvokerName(MethodBase method) => "__ios_original_static_" + ForMethod(method);
        internal static string TypedOriginalAdapterName(MethodBase method) => "__ios_typed_original_" + ForMethod(method);
        internal static string GenericOpenGateFieldName(MethodBase method) => "__ios_open_generic_hooked_" + ForMethod(method);
        internal static string GenericOpenGateFieldName(MethodDefinition method) => "__ios_open_generic_hooked_" + ForMethod(method);
        internal static string GateFieldName(MethodDefinition method) => "__ios_hooked_" + ForMethod(method);
        internal static string OriginalMethodName(MethodDefinition method) => "__ios_original_" + ForMethod(method);
        internal static string ValueTypeInvokerName(MethodDefinition method) => "__ios_original_static_" + ForMethod(method);
        internal static string TypedOriginalAdapterName(MethodDefinition method) => "__ios_typed_original_" + ForMethod(method);

        internal static MethodBase GetDefinition(MethodBase method)
        {
            if (method is MethodInfo methodInfo && methodInfo.IsGenericMethod && !methodInfo.IsGenericMethodDefinition)
                method = methodInfo.GetGenericMethodDefinition();

            Type declaringType = method.DeclaringType;
            if (declaringType != null && declaringType.IsGenericType && !declaringType.IsGenericTypeDefinition)
            {
                Type genericDefinition = declaringType.GetGenericTypeDefinition();
                method = genericDefinition.Module.ResolveMethod(
                    method.MetadataToken,
                    genericDefinition.GetGenericArguments(),
                    method is MethodInfo genericMethod && genericMethod.IsGenericMethod
                        ? genericMethod.GetGenericArguments()
                        : null);
            }
            return method;
        }

        internal static MethodBase GetMethodDefinitionPreservingDeclaringType(MethodBase method)
        {
            MethodInfo methodInfo = method as MethodInfo;
            return methodInfo != null && methodInfo.IsGenericMethod && !methodInfo.IsGenericMethodDefinition
                ? methodInfo.GetGenericMethodDefinition()
                : method;
        }

        private static string ReturnType(MethodBase method)
        {
            return method is MethodInfo info ? SignatureTypeName(info.ReturnParameter) : "System.Void";
        }

        private static string SignatureTypeName(ParameterInfo parameter)
        {
            string result = TypeName(parameter.ParameterType);
            Type[] required = parameter.GetRequiredCustomModifiers();
            Type[] optional = parameter.GetOptionalCustomModifiers();
            if (required.Length > 0)
                result += " " + string.Join(" ", required.Select(modifier => "modreq(" + TypeName(modifier) + ")"));
            if (optional.Length > 0)
                result += " " + string.Join(" ", optional.Select(modifier => "modopt(" + TypeName(modifier) + ")"));
            return result;
        }

        private static int GenericArity(MethodBase method)
        {
            return method is MethodInfo info && info.IsGenericMethod ? info.GetGenericArguments().Length : 0;
        }

        private static string TypeName(Type type)
        {
            if (type.IsByRef) return TypeName(type.GetElementType()) + "&";
            if (type.IsPointer) return TypeName(type.GetElementType()) + "*";
            if (type.IsFunctionPointer)
            {
                Type[] parameters = type.GetFunctionPointerParameterTypes();
                return FunctionPointerConvention(type) + TypeName(type.GetFunctionPointerReturnType()) +
                    "(" + string.Join(",", parameters.Select(TypeName)) + ")";
            }
            if (type.IsArray) return TypeName(type.GetElementType()) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
            if (type.IsGenericParameter)
                return (type.DeclaringMethod == null ? "!" : "!!") + type.GenericParameterPosition;
            if (type.IsGenericType)
            {
                Type definition = type.GetGenericTypeDefinition();
                if (type.IsGenericTypeDefinition)
                    return NormalizeTypeName(definition.FullName);
                return NormalizeTypeName(definition.FullName) + "<" +
                    string.Join(",", type.GetGenericArguments().Select(TypeName)) + ">";
            }
            return NormalizeTypeName(type.FullName ?? type.Name);
        }

        private static string TypeName(TypeReference type)
        {
            if (type is ByReferenceType byReference) return TypeName(byReference.ElementType) + "&";
            if (type is PointerType pointer) return TypeName(pointer.ElementType) + "*";
            if (type is FunctionPointerType functionPointer)
            {
                return FunctionPointerConvention(functionPointer) + TypeName(functionPointer.ReturnType) +
                    "(" + string.Join(",", functionPointer.Parameters.Select(parameter => TypeName(parameter.ParameterType))) + ")";
            }
            if (type is ArrayType array) return TypeName(array.ElementType) + "[" + new string(',', array.Rank - 1) + "]";
            if (type is GenericParameter parameter)
                return (parameter.Type == GenericParameterType.Type ? "!" : "!!") + parameter.Position;
            if (type is GenericInstanceType instance)
                return NormalizeTypeName(instance.ElementType.FullName) + "<" +
                    string.Join(",", instance.GenericArguments.Select(TypeName)) + ">";
            if (type is OptionalModifierType optional) return TypeName(optional.ElementType) + " modopt(" + TypeName(optional.ModifierType) + ")";
            if (type is RequiredModifierType required) return TypeName(required.ElementType) + " modreq(" + TypeName(required.ModifierType) + ")";
            if (type is PinnedType pinned) return TypeName(pinned.ElementType) + " pinned";
            if (type is SentinelType sentinel) return TypeName(sentinel.ElementType) + " sentinel";
            return NormalizeTypeName(type.FullName);
        }

        private static string FunctionPointerConvention(Type type)
        {
            if (!type.IsUnmanagedFunctionPointer) return "managed:";
            return "unmanaged[" + string.Join(",", type.GetFunctionPointerCallingConventions()
                .Select(convention => convention.Name).OrderBy(name => name, StringComparer.Ordinal)) + "]:";
        }

        private static string FunctionPointerConvention(FunctionPointerType type)
        {
            switch (type.CallingConvention)
            {
                case MethodCallingConvention.Default:
                    return "managed:";
                case MethodCallingConvention.C:
                    return "unmanaged[CallConvCdecl]:";
                case MethodCallingConvention.StdCall:
                    return "unmanaged[CallConvStdcall]:";
                case MethodCallingConvention.ThisCall:
                    return "unmanaged[CallConvThiscall]:";
                case MethodCallingConvention.FastCall:
                    return "unmanaged[CallConvFastcall]:";
                case MethodCallingConvention.VarArg:
                    return "unmanaged[vararg]:";
                default:
                    return "unmanaged[" + type.CallingConvention + "]:";
            }
        }

        private static string NormalizeTypeName(string name)
        {
            return name.Replace('+', '/');
        }

        private static string Hash(string signature)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(signature));
                return string.Concat(bytes.Select(value => value.ToString("x2")));
            }
        }
    }
}
