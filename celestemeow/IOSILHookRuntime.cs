using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace celestemeow
{
    /// <summary>
    /// Applies Everest ILContext manipulators to Cecil bodies and loads the result
    /// as interpreted managed IL. It does not create native trampolines.
    /// </summary>
    internal static class IOSILHookRuntime
    {
        internal const string AddILDataKey = "Celeste.IOSHookRuntime.AddIL";
        internal const string RemoveILDataKey = "Celeste.IOSHookRuntime.RemoveIL";

        private sealed class Registration
        {
            public MethodBase Target;
            public Delegate Manipulator;
            public long Sequence;
            public string Id;
            public int? Priority;
            public int SubPriority;
            public string[] Before;
            public string[] After;
        }

        private static readonly object Sync = new object();
        private static readonly Dictionary<MethodBase, Registration[]> Hooks = new Dictionary<MethodBase, Registration[]>();
        private static readonly List<object> RetainedContexts = new List<object>();
        private static readonly List<AssemblyDefinition> RetainedDefinitions = new List<AssemblyDefinition>();
        private static readonly Dictionary<Assembly, byte[]> AssemblySnapshots = new Dictionary<Assembly, byte[]>();
        private static long nextSequence;
        private static string gameAssemblyPath;

        public static void Initialize()
        {
            AppDomain.CurrentDomain.SetData(AddILDataKey, (Action<MethodBase, Delegate, object>)Add);
            AppDomain.CurrentDomain.SetData(RemoveILDataKey, (Action<MethodBase, Delegate>)Remove);
        }

        public static void ConfigureGameAssembly(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A Celeste assembly path is required for IL hooks.", nameof(path));
            gameAssemblyPath = Path.GetFullPath(path);
        }

        public static void RegisterAssemblySnapshot(Assembly assembly, byte[] image)
        {
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));
            if (image == null) throw new ArgumentNullException(nameof(image));
            lock (Sync) AssemblySnapshots[assembly] = image;
            AssemblyLoadContext context = AssemblyLoadContext.GetLoadContext(assembly);
            if (context != null && context.IsCollectible)
                context.Unloading += _ => { lock (Sync) AssemblySnapshots.Remove(assembly); };
        }

        internal static KeyValuePair<string, byte[]>[] GetAssemblySnapshotImages()
        {
            lock (Sync)
                return AssemblySnapshots
                    .Select(entry => new KeyValuePair<string, byte[]>(entry.Key.GetName().Name, entry.Value))
                    .ToArray();
        }

        public static void Add(MethodBase target, Delegate manipulator, object config)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (manipulator == null) throw new ArgumentNullException(nameof(manipulator));

            lock (Sync)
            {
                Registration[] existing;
                if (!Hooks.TryGetValue(target, out existing)) existing = Array.Empty<Registration>();
                if (existing.Any(item => ReferenceEquals(item.Manipulator, manipulator))) return;

                Registration registration = MakeRegistration(target, manipulator, config);
                Registration[] next = Order(existing.Concat(new[] { registration }).ToArray());
                MethodInfo body = BuildInterpretedBody(target, next);
                Hooks[target] = next;
                IOSHookRuntime.SetModifiedBody(target, body);
            }
        }

        public static void Remove(MethodBase target, Delegate manipulator)
        {
            if (target == null || manipulator == null) return;
            lock (Sync)
            {
                Registration[] existing;
                if (!Hooks.TryGetValue(target, out existing)) return;
                Registration[] next = existing.Where(item => !ReferenceEquals(item.Manipulator, manipulator)).ToArray();
                MethodInfo body = next.Length == 0 ? null : BuildInterpretedBody(target, Order(next));
                if (next.Length == 0) Hooks.Remove(target);
                else Hooks[target] = Order(next);
                IOSHookRuntime.SetModifiedBody(target, body);
            }
        }

        public static void Clear()
        {
            lock (Sync)
            {
                foreach (MethodBase target in Hooks.Keys.ToArray())
                    IOSHookRuntime.SetModifiedBody(target, null);
                Hooks.Clear();
                // Contexts remain rooted through the end of the process so an already-running
                // method cannot observe a released EmitDelegate reference cell.
            }
        }

        public static int GetHookCount(MethodBase target)
        {
            if (target == null) return 0;
            lock (Sync)
            {
                Registration[] registrations;
                return Hooks.TryGetValue(target, out registrations) ? registrations.Length : 0;
            }
        }

        private static Registration MakeRegistration(MethodBase target, Delegate manipulator, object config)
        {
            string id = ReadValue(config, "ID", "Id") as string;
            if (string.IsNullOrWhiteSpace(id))
                id = (manipulator.Method.DeclaringType == null ? "" : manipulator.Method.DeclaringType.FullName + ".") + manipulator.Method.Name;
            object priorityValue = ReadValue(config, "Priority");
            int? priority = priorityValue == null ? (int?)null : Convert.ToInt32(priorityValue);
            return new Registration
            {
                Target = target,
                Manipulator = manipulator,
                Sequence = System.Threading.Interlocked.Increment(ref nextSequence),
                Id = id,
                Priority = priority,
                SubPriority = Convert.ToInt32(ReadValue(config, "SubPriority") ?? System.Threading.Interlocked.Increment(ref nextSequence)),
                Before = ReadStringArray(ReadValue(config, "Before")),
                After = ReadStringArray(ReadValue(config, "After"))
            };
        }

        private static object ReadValue(object source, params string[] names)
        {
            if (source == null) return null;
            Type type = source.GetType();
            foreach (string name in names)
            {
                PropertyInfo property = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
                if (property != null && property.GetIndexParameters().Length == 0) return property.GetValue(source, null);
                FieldInfo field = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
                if (field != null) return field.GetValue(source);
            }
            return null;
        }

        private static string[] ReadStringArray(object value)
        {
            if (value == null || value is string) return Array.Empty<string>();
            System.Collections.IEnumerable values = value as System.Collections.IEnumerable;
            return values == null ? Array.Empty<string>() : values.Cast<object>().Where(item => item != null).Select(item => item.ToString()).ToArray();
        }

        private static Registration[] Order(Registration[] registrations)
        {
            Registration[] baseline = registrations
                .OrderBy(item => item.Priority.HasValue ? 0 : 1)
                .ThenByDescending(item => item.Priority)
                .ThenByDescending(item => item.SubPriority)
                .ThenBy(item => item.Sequence)
                .ToArray();
            Dictionary<Registration, HashSet<Registration>> before = baseline.ToDictionary(item => item, item => new HashSet<Registration>());
            foreach (Registration item in baseline)
            foreach (Registration other in baseline)
            {
                if (ReferenceEquals(item, other)) continue;
                if (item.Before.Contains(other.Id, StringComparer.Ordinal)) before[other].Add(item);
                if (item.After.Contains(other.Id, StringComparer.Ordinal)) before[item].Add(other);
                if (other.Before.Contains(item.Id, StringComparer.Ordinal)) before[item].Add(other);
                if (other.After.Contains(item.Id, StringComparer.Ordinal)) before[other].Add(item);
            }

            List<Registration> result = new List<Registration>();
            HashSet<Registration> visiting = new HashSet<Registration>();
            HashSet<Registration> visited = new HashSet<Registration>();
            foreach (Registration registration in baseline) Visit(registration);
            return result.ToArray();

            void Visit(Registration registration)
            {
                if (visited.Contains(registration)) return;
                if (!visiting.Add(registration)) throw new InvalidOperationException("IL hook ordering contains a cycle at '" + registration.Id + "'.");
                foreach (Registration earlier in baseline.Where(before[registration].Contains)) Visit(earlier);
                visiting.Remove(registration);
                visited.Add(registration);
                result.Add(registration);
            }
        }

        private static MethodInfo BuildInterpretedBody(MethodBase target, Registration[] registrations)
        {
            MethodInfo targetMethod = target as MethodInfo;
            if (targetMethod == null && !(target is ConstructorInfo))
                throw new NotSupportedException("IL hooks require a managed method or constructor: " + target);
            if (target.ContainsGenericParameters)
                throw new NotSupportedException("An IL hook must target a closed generic method or type on iOS: " + target);

            ReaderParameters readerParameters = new ReaderParameters
            {
                InMemory = true,
                ReadingMode = ReadingMode.Immediate,
                ReadSymbols = false
            };
            byte[] assemblyImage;
            AssemblyDefinition celeste;
            lock (Sync) AssemblySnapshots.TryGetValue(target.Module.Assembly, out assemblyImage);
            if (assemblyImage != null)
                celeste = AssemblyDefinition.ReadAssembly(new MemoryStream(assemblyImage, false), readerParameters);
            else
                celeste = AssemblyDefinition.ReadAssembly(ResolveGameAssemblyPath(target), readerParameters);
            ModuleDefinition sourceModule = celeste.MainModule;
            Type closedDeclaringType = target.DeclaringType;
            Type genericDeclaringType = closedDeclaringType.IsConstructedGenericType
                ? closedDeclaringType.GetGenericTypeDefinition()
                : closedDeclaringType;
            string declaringTypeName = genericDeclaringType.FullName.Replace('+', '/');
            TypeDefinition declaringType = sourceModule.GetType(declaringTypeName);
            if (declaringType == null) throw new TypeLoadException("Could not find the IL hook target type in Celeste.dll: " + declaringTypeName);

            string originalName = IOSHookIdentity.OriginalMethodName(target);
            MethodDefinition sourceMethod = declaringType.Methods.FirstOrDefault(method => method.Name == originalName);
            if (sourceMethod == null) throw new MissingMethodException("The iOS hook weave did not preserve an original body for " + target + ".");

            // ILContext exposes the source method just like Everest does on desktop.
            sourceMethod.Name = target.Name;
            Type contextType = Type.GetType("MonoMod.Cil.ILContext, MonoMod.Utils", true);
            MethodInfo contextInvoke = contextType.GetMethod("Invoke", BindingFlags.Instance | BindingFlags.Public);
            foreach (Registration registration in registrations)
            {
                object context = Activator.CreateInstance(contextType, new object[] { sourceMethod });
                try
                {
                    contextInvoke.Invoke(context, new object[] { registration.Manipulator });
                }
                catch (TargetInvocationException exception) when (exception.InnerException != null)
                {
                    throw new InvalidOperationException("IL manipulator '" + registration.Id + "' failed for " + target + ".", exception.InnerException);
                }
                RetainedContexts.Add(context);
            }

            MethodInfo loadedBody = WriteAndLoadSidecarBody(target, sourceMethod, sourceModule);
            RetainedDefinitions.Add(celeste);
            return loadedBody;
        }

        private static string ResolveGameAssemblyPath(MethodBase target)
        {
            string modulePath = target.Module.FullyQualifiedName;
            if (!string.IsNullOrEmpty(modulePath) && File.Exists(modulePath)) return modulePath;
            string assemblyName = target.Module.Assembly.GetName().Name;
            if (!string.IsNullOrEmpty(gameAssemblyPath) && File.Exists(gameAssemblyPath) &&
                string.Equals(assemblyName, AssemblyName.GetAssemblyName(gameAssemblyPath).Name, StringComparison.OrdinalIgnoreCase))
                return gameAssemblyPath;

            // Celeste and FNA are shipped side by side in the app bundle. Mono
            // can report an empty or virtual Module.FullyQualifiedName for a
            // bundled assembly, so resolve the target by its actual assembly
            // name instead of assuming every IL hook targets Celeste.dll.
            string gameDirectory = string.IsNullOrEmpty(gameAssemblyPath)
                ? null
                : Path.GetDirectoryName(gameAssemblyPath);
            string[] candidates = new[]
            {
                string.IsNullOrEmpty(gameDirectory) ? null : Path.Combine(gameDirectory, assemblyName + ".dll"),
                Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll"),
                Path.Combine(AppContext.BaseDirectory, "celeste", assemblyName + ".dll")
            };
            foreach (string candidate in candidates)
                if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate) &&
                    string.Equals(assemblyName, AssemblyName.GetAssemblyName(candidate).Name, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            throw new FileNotFoundException("Could not locate the loaded target assembly required to apply an IL hook: " + target.Module.Assembly.FullName);
        }

        private static MethodInfo WriteAndLoadSidecarBody(MethodBase target, MethodDefinition sourceMethod, ModuleDefinition sourceModule)
        {
            string identity = IOSHookIdentity.ForMethod(target);
            string assemblyName = "CelesteIOS.IL." + identity + "." + System.Threading.Interlocked.Increment(ref nextSequence).ToString();
            AssemblyDefinition sidecar = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition(assemblyName, new Version(1, 0, 0, 0)),
                assemblyName,
                ModuleKind.Dll);
            ModuleDefinition module = sidecar.MainModule;

            TypeDefinition accessAttribute = CreateAccessAttribute(module);
            module.Types.Add(accessAttribute);
            AddAccessGrant(sidecar, accessAttribute.Methods[0], sourceModule.Assembly.Name.Name);
            foreach (AssemblyNameReference reference in sourceModule.AssemblyReferences)
                AddAccessGrant(sidecar, accessAttribute.Methods[0], reference.Name);

            TypeDefinition helper = new TypeDefinition(
                "CelesteIOS.Generated",
                "HookBody_" + identity,
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Abstract | Mono.Cecil.TypeAttributes.Sealed | Mono.Cecil.TypeAttributes.BeforeFieldInit,
                module.ImportReference(typeof(object)));
            module.Types.Add(helper);

            Dictionary<GenericParameter, GenericParameter> genericParameters = new Dictionary<GenericParameter, GenericParameter>();
            foreach (GenericParameter sourceParameter in sourceMethod.DeclaringType.GenericParameters)
            {
                GenericParameter helperParameter = new GenericParameter(sourceParameter.Name, helper)
                {
                    Attributes = sourceParameter.Attributes
                };
                helper.GenericParameters.Add(helperParameter);
                genericParameters.Add(sourceParameter, helperParameter);
            }

            MethodDefinition bodyMethod = new MethodDefinition(
                "Invoke",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static | Mono.Cecil.MethodAttributes.HideBySig,
                module.TypeSystem.Void);
            helper.Methods.Add(bodyMethod);

            foreach (GenericParameter sourceParameter in sourceMethod.GenericParameters)
            {
                GenericParameter helperParameter = new GenericParameter(sourceParameter.Name, bodyMethod)
                {
                    Attributes = sourceParameter.Attributes
                };
                bodyMethod.GenericParameters.Add(helperParameter);
                genericParameters.Add(sourceParameter, helperParameter);
            }
            foreach (GenericParameter sourceParameter in sourceMethod.DeclaringType.GenericParameters.Concat(sourceMethod.GenericParameters))
            {
                GenericParameter helperParameter = genericParameters[sourceParameter];
                foreach (GenericParameterConstraint constraint in sourceParameter.Constraints)
                    helperParameter.Constraints.Add(new GenericParameterConstraint(
                        module.ImportReference(constraint.ConstraintType, bodyMethod)));
            }

            bodyMethod.ReturnType = module.ImportReference(sourceMethod.ReturnType, bodyMethod);

            Dictionary<ParameterDefinition, ParameterDefinition> parameters = new Dictionary<ParameterDefinition, ParameterDefinition>();
            if (!target.IsStatic)
            {
                TypeReference selfType = module.ImportReference(sourceMethod.DeclaringType);
                if (sourceMethod.DeclaringType.HasGenericParameters)
                {
                    GenericInstanceType genericSelfType = new GenericInstanceType(selfType);
                    foreach (GenericParameter parameter in helper.GenericParameters)
                        genericSelfType.GenericArguments.Add(parameter);
                    selfType = genericSelfType;
                }
                if (target.DeclaringType.IsValueType) selfType = new ByReferenceType(selfType);
                ParameterDefinition self = new ParameterDefinition("self", Mono.Cecil.ParameterAttributes.None, selfType);
                bodyMethod.Parameters.Add(self);
                parameters[sourceMethod.Body.ThisParameter] = self;
            }
            foreach (ParameterDefinition parameter in sourceMethod.Parameters)
            {
                ParameterDefinition imported = new ParameterDefinition(parameter.Name, parameter.Attributes,
                    module.ImportReference(parameter.ParameterType, bodyMethod));
                bodyMethod.Parameters.Add(imported);
                parameters[parameter] = imported;
            }

            bodyMethod.Body = CloneBody(sourceMethod, bodyMethod, module, parameters);
            using (MemoryStream stream = new MemoryStream())
            {
                sidecar.Write(stream);
                AssemblyLoadContext loadContext = AssemblyLoadContext.GetLoadContext(target.Module.Assembly) ?? AssemblyLoadContext.Default;
                Assembly assembly = loadContext.LoadFromStream(new MemoryStream(stream.ToArray()));
                Type helperType = assembly.GetType(helper.FullName, true);
                if (helperType.IsGenericTypeDefinition && target.DeclaringType.IsConstructedGenericType)
                    helperType = helperType.MakeGenericType(target.DeclaringType.GetGenericArguments());
                MethodInfo loadedMethod = helperType.GetMethod("Invoke", BindingFlags.Public | BindingFlags.Static);
                if (loadedMethod.IsGenericMethodDefinition && target is MethodInfo closedMethod && closedMethod.IsGenericMethod &&
                    !closedMethod.IsGenericMethodDefinition)
                    loadedMethod = loadedMethod.MakeGenericMethod(closedMethod.GetGenericArguments());
                if (loadedMethod.ContainsGenericParameters)
                    throw new InvalidOperationException("The iOS IL hook sidecar did not close all generic parameters for " + target + ".");
                return loadedMethod;
            }
        }

        private static TypeDefinition CreateAccessAttribute(ModuleDefinition module)
        {
            TypeDefinition type = new TypeDefinition(
                "System.Runtime.CompilerServices",
                "IgnoresAccessChecksToAttribute",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Sealed | Mono.Cecil.TypeAttributes.BeforeFieldInit,
                module.ImportReference(typeof(Attribute)));
            MethodDefinition constructor = new MethodDefinition(
                ".ctor",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig | Mono.Cecil.MethodAttributes.SpecialName | Mono.Cecil.MethodAttributes.RTSpecialName,
                module.TypeSystem.Void);
            constructor.Parameters.Add(new ParameterDefinition("assemblyName", Mono.Cecil.ParameterAttributes.None, module.TypeSystem.String));
            ILProcessor il = constructor.Body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldarg_0));
            ConstructorInfo attributeConstructor = typeof(Attribute).GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                Type.EmptyTypes,
                null);
            il.Append(il.Create(OpCodes.Call, module.ImportReference(attributeConstructor)));
            il.Append(il.Create(OpCodes.Ret));
            type.Methods.Add(constructor);
            return type;
        }

        private static void AddAccessGrant(AssemblyDefinition assembly, MethodDefinition constructor, string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            if (assembly.CustomAttributes.Any(attribute =>
                attribute.Constructor.DeclaringType.FullName == constructor.DeclaringType.FullName &&
                (string)attribute.ConstructorArguments[0].Value == name)) return;
            CustomAttribute attribute = new CustomAttribute(constructor);
            attribute.ConstructorArguments.Add(new CustomAttributeArgument(assembly.MainModule.TypeSystem.String, name));
            assembly.CustomAttributes.Add(attribute);
        }

        private static Mono.Cecil.Cil.MethodBody CloneBody(MethodDefinition source, MethodDefinition destination, ModuleDefinition module, Dictionary<ParameterDefinition, ParameterDefinition> parameters)
        {
            Mono.Cecil.Cil.MethodBody body = new Mono.Cecil.Cil.MethodBody(destination)
            {
                InitLocals = source.Body.InitLocals,
                MaxStackSize = source.Body.MaxStackSize
            };
            Dictionary<VariableDefinition, VariableDefinition> variables = new Dictionary<VariableDefinition, VariableDefinition>();
            foreach (VariableDefinition variable in source.Body.Variables)
            {
                VariableDefinition imported = new VariableDefinition(module.ImportReference(variable.VariableType, destination));
                body.Variables.Add(imported);
                variables[variable] = imported;
            }

            Dictionary<Instruction, Instruction> instructions = new Dictionary<Instruction, Instruction>();
            foreach (Instruction instruction in source.Body.Instructions)
                instructions[instruction] = Instruction.Create(OpCodes.Nop);
            ILProcessor il = body.GetILProcessor();
            foreach (Instruction instruction in source.Body.Instructions)
            {
                Instruction copied = instructions[instruction];
                copied.OpCode = instruction.OpCode;
                copied.Operand = MapOperand(instruction.Operand, module, destination, instructions, variables, parameters);
                il.Append(copied);
            }

            foreach (ExceptionHandler handler in source.Body.ExceptionHandlers)
            {
                body.ExceptionHandlers.Add(new ExceptionHandler(handler.HandlerType)
                {
                    CatchType = handler.CatchType == null ? null : module.ImportReference(handler.CatchType, destination),
                    TryStart = handler.TryStart == null ? null : instructions[handler.TryStart],
                    TryEnd = handler.TryEnd == null ? null : instructions[handler.TryEnd],
                    HandlerStart = handler.HandlerStart == null ? null : instructions[handler.HandlerStart],
                    HandlerEnd = handler.HandlerEnd == null ? null : instructions[handler.HandlerEnd],
                    FilterStart = handler.FilterStart == null ? null : instructions[handler.FilterStart]
                });
            }
            return body;
        }

        private static object MapOperand(
            object operand,
            ModuleDefinition module,
            MethodDefinition context,
            Dictionary<Instruction, Instruction> instructions,
            Dictionary<VariableDefinition, VariableDefinition> variables,
            Dictionary<ParameterDefinition, ParameterDefinition> parameters)
        {
            if (operand is Instruction target) operand = instructions[target];
            else if (operand is Instruction[] targets) operand = targets.Select(item => instructions[item]).ToArray();
            else if (operand is VariableDefinition variable) operand = variables[variable];
            else if (operand is ParameterDefinition parameter) operand = parameters[parameter];
            else if (operand is MethodReference method) operand = module.ImportReference(method, context);
            else if (operand is FieldReference field) operand = module.ImportReference(field, context);
            else if (operand is TypeReference type) operand = module.ImportReference(type, context);
            else if (operand is CallSite callSite) operand = CloneCallSite(callSite, module, context);
            return operand;
        }

        private static CallSite CloneCallSite(CallSite source, ModuleDefinition module, MethodDefinition context)
        {
            CallSite clone = new CallSite(module.ImportReference(source.ReturnType, context))
            {
                HasThis = source.HasThis,
                ExplicitThis = source.ExplicitThis,
                CallingConvention = source.CallingConvention
            };
            foreach (ParameterDefinition parameter in source.Parameters)
                clone.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes,
                    module.ImportReference(parameter.ParameterType, context)));
            return clone;
        }
    }
}
