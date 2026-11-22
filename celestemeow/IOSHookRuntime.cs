using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Collections;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Linq.Expressions;
using System.Collections.Concurrent;
using System.IO;

namespace celestemeow
{
    /// <summary>
    /// Managed dispatch core for the iOS hook gates. The gates themselves are
    /// woven into Celeste.dll at build time, so this path never writes executable
    /// memory or relies on Reflection.Emit.
    /// </summary>
    internal static class IOSHookRuntime
    {
        internal const string DispatchDataKey = "Celeste.IOSHookRuntime.Dispatch";
        internal const string AddOnDataKey = "Celeste.IOSHookRuntime.AddOn";
        internal const string RemoveOnDataKey = "Celeste.IOSHookRuntime.RemoveOn";
        internal const string AddReflectedOnDataKey = "Celeste.IOSHookRuntime.AddReflectedOn";
        internal const string RemoveReflectedOnDataKey = "Celeste.IOSHookRuntime.RemoveReflectedOn";
        internal const string CreateOriginalDataKey = "Celeste.IOSHookRuntime.CreateOriginal";
        internal const string WeaveAssemblyDataKey = "Celeste.IOSHookRuntime.WeaveAssembly";
        internal const string RegisterAssemblyDataKey = "Celeste.IOSHookRuntime.RegisterAssembly";
        internal const string AddDetourDataKey = "Celeste.IOSHookRuntime.AddDetour";
        internal const string RemoveDetourDataKey = "Celeste.IOSHookRuntime.RemoveDetour";
        internal const string DetourIndexDataKey = "Celeste.IOSHookRuntime.DetourIndex";
        internal const string DetourCountDataKey = "Celeste.IOSHookRuntime.DetourCount";
        internal const string CreateDetourTrampolineDataKey = "Celeste.IOSHookRuntime.CreateDetourTrampoline";
        internal const string ILHookCountDataKey = "Celeste.IOSHookRuntime.ILHookCount";
        internal const string ClearDataKey = "Celeste.IOSHookRuntime.Clear";

        private static readonly object Sync = new object();
        private static readonly Dictionary<MethodBase, Registration[]> OnHooks = new Dictionary<MethodBase, Registration[]>();
        private static readonly Dictionary<MethodBase, MethodInfo> ModifiedBodies = new Dictionary<MethodBase, MethodInfo>();
        private static readonly ConcurrentDictionary<MethodBase, MethodInfo> OriginalMethods = new ConcurrentDictionary<MethodBase, MethodInfo>();
        private static long nextSequence;

        private sealed class Registration
        {
            public Delegate Hook;
            public MethodInfo HookMethod;
            public object HookTarget;
            public object Identity;
            public bool UseOriginalInstance;
            public long Sequence;
            public string Id;
            public int? Priority;
            public int SubPriority;
            public string[] Before;
            public string[] After;
        }

        private sealed class Invocation
        {
            private readonly MethodBase target;
            private readonly MethodInfo original;
            private readonly Registration[] hooks;
            public object CurrentInstance { get; private set; }

            public Invocation(MethodBase target, MethodInfo original, Registration[] hooks, object instance)
            {
                this.target = target;
                this.original = original;
                this.hooks = hooks;
                CurrentInstance = instance;
            }

            public object InvokeNext(int index, object instance, object[] arguments)
            {
                if (index >= hooks.Length)
                    return InvokeOriginal(instance, arguments);

                Registration registration = hooks[index];
                ParameterInfo[] hookParameters = registration.HookMethod.GetParameters();
                bool instanceBound = registration.UseOriginalInstance && !registration.HookMethod.IsStatic;
                int targetArgumentCount = target.GetParameters().Length + (target.IsStatic || instanceBound ? 0 : 1);
                bool hasOrig = hookParameters.Length == targetArgumentCount + 1 &&
                    hookParameters.Length > 0 && typeof(Delegate).IsAssignableFrom(hookParameters[0].ParameterType);
                int hookTargetStart = hasOrig ? 1 : 0;
                if (hookParameters.Length != targetArgumentCount + hookTargetStart)
                    throw new InvalidOperationException("The hook signature no longer matches " + target + ".");

                object[] hookArguments = new object[hookParameters.Length];
                if (hasOrig)
                    hookArguments[0] = CreateContinuation(hookParameters[0].ParameterType, index + 1);

                int offset = hookTargetStart;
                if (!target.IsStatic && !instanceBound)
                    hookArguments[offset++] = instance;
                Array.Copy(arguments, 0, hookArguments, offset, arguments.Length);

                object result;
                try
                {
                    result = registration.Hook != null
                        ? registration.Hook.DynamicInvoke(hookArguments)
                        : registration.HookMethod.Invoke(instanceBound ? instance : registration.HookTarget, hookArguments);
                }
                catch (TargetInvocationException exception) when (exception.InnerException != null)
                {
                    ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                    throw;
                }

                offset = hookTargetStart + (target.IsStatic || instanceBound ? 0 : 1);
                for (int i = 0; i < arguments.Length; i++)
                    if (target.GetParameters()[i].ParameterType.IsByRef)
                        arguments[i] = hookArguments[offset + i];
                if (!target.IsStatic && target.DeclaringType.IsValueType)
                    CurrentInstance = instanceBound ? instance : hookArguments[hookTargetStart];
                return result;
            }

            private object InvokeOriginal(object instance, object[] arguments)
            {
                try
                {
                    MethodInfo modified = GetModifiedBody(target);
                    if (modified != null)
                    {
                        object[] bodyArguments;
                        if (target.IsStatic)
                            bodyArguments = arguments;
                        else
                        {
                            bodyArguments = new object[arguments.Length + 1];
                            bodyArguments[0] = instance;
                            Array.Copy(arguments, 0, bodyArguments, 1, arguments.Length);
                        }
                        object modifiedResult = modified.Invoke(null, bodyArguments);
                        if (!target.IsStatic && target.DeclaringType.IsValueType)
                            CurrentInstance = bodyArguments[0];
                        if (target.GetParameters().Any(parameter => parameter.ParameterType.IsByRef))
                            Array.Copy(bodyArguments, target.IsStatic ? 0 : 1, arguments, 0, arguments.Length);
                        return modifiedResult;
                    }
                    if (!target.IsStatic && target.DeclaringType.IsValueType)
                    {
                        object[] bodyArguments = new object[arguments.Length + 1];
                        bodyArguments[0] = instance;
                        Array.Copy(arguments, 0, bodyArguments, 1, arguments.Length);
                        object originalResult = original.Invoke(null, bodyArguments);
                        CurrentInstance = bodyArguments[0];
                        if (target.GetParameters().Any(parameter => parameter.ParameterType.IsByRef))
                            Array.Copy(bodyArguments, 1, arguments, 0, arguments.Length);
                        return originalResult;
                    }
                    return original.Invoke(instance, arguments);
                }
                catch (TargetInvocationException exception) when (exception.InnerException != null)
                {
                    ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                    throw;
                }
            }

            public Delegate CreateContinuation(Type delegateType, int nextIndex)
            {
                MethodInfo invoke = delegateType.GetMethod("Invoke");
                if (invoke == null)
                    throw new InvalidOperationException("An On hook's first argument must be its orig delegate.");
                if (invoke.ReturnType.IsByRef)
                    throw new NotSupportedException("By-reference return hooks require a generated typed gate.");

                ParameterInfo[] signature = invoke.GetParameters();
                int instanceOffset = target.IsStatic ? 0 : 1;
                if (signature.Length != target.GetParameters().Length + instanceOffset)
                    throw new InvalidOperationException("The orig delegate signature no longer matches " + target + ".");

                ParameterExpression[] parameters = signature
                    .Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name))
                    .ToArray();
                ParameterExpression argumentArray = Expression.Variable(typeof(object[]), "arguments");
                List<Expression> body = new List<Expression>();
                List<Expression> boxedArguments = new List<Expression>();

                for (int i = instanceOffset; i < parameters.Length; i++)
                {
                    int parameterIndex = i - instanceOffset;
                    ParameterInfo methodParameter = target.GetParameters()[parameterIndex];
                    if (methodParameter.IsOut)
                    {
                        Type elementType = methodParameter.ParameterType.GetElementType();
                        boxedArguments.Add(Expression.Convert(Expression.Default(elementType), typeof(object)));
                    }
                    else
                    {
                        boxedArguments.Add(Expression.Convert(parameters[i], typeof(object)));
                    }
                }

                body.Add(Expression.Assign(argumentArray, Expression.NewArrayInit(typeof(object), boxedArguments)));
                Expression instanceExpression;
                if (target.IsStatic)
                    instanceExpression = Expression.Constant(null, typeof(object));
                else if (target.DeclaringType.IsValueType && signature[0].ParameterType.IsByRef)
                {
                    MethodInfo boxByRef = typeof(Invocation).GetMethod(nameof(BoxRef), BindingFlags.Static | BindingFlags.NonPublic)
                        .MakeGenericMethod(signature[0].ParameterType.GetElementType());
                    instanceExpression = Expression.Call(boxByRef, parameters[0]);
                }
                else
                    instanceExpression = Expression.Convert(parameters[0], typeof(object));
                MethodInfo invokeNext = typeof(Invocation).GetMethod(nameof(InvokeNext));
                ParameterExpression result = Expression.Variable(typeof(object), "result");
                body.Add(Expression.Assign(
                    result,
                    Expression.Call(Expression.Constant(this), invokeNext,
                        Expression.Constant(nextIndex), instanceExpression, argumentArray)));

                if (!target.IsStatic && target.DeclaringType.IsValueType && signature[0].ParameterType.IsByRef)
                {
                    MethodInfo copyBack = typeof(Invocation).GetMethod(nameof(CopyBackRef), BindingFlags.Static | BindingFlags.NonPublic)
                        .MakeGenericMethod(signature[0].ParameterType.GetElementType());
                    body.Add(Expression.Call(copyBack, parameters[0], Expression.Property(Expression.Constant(this), nameof(CurrentInstance))));
                }

                ParameterInfo[] methodParameters = target.GetParameters();
                for (int i = 0; i < methodParameters.Length; i++)
                {
                    if (!methodParameters[i].ParameterType.IsByRef)
                        continue;
                    Type elementType = methodParameters[i].ParameterType.GetElementType();
                    Expression value = Expression.Convert(Expression.ArrayIndex(argumentArray, Expression.Constant(i)), elementType);
                    body.Add(Expression.Assign(parameters[i + instanceOffset], value));
                }

                body.Add(invoke.ReturnType == typeof(void)
                    ? (Expression)Expression.Empty()
                    : Expression.Convert(result, invoke.ReturnType));

                return Expression.Lambda(delegateType, Expression.Block(new[] { argumentArray, result }, body), parameters)
                    .Compile(preferInterpretation: true);
            }

            private static object BoxRef<T>(ref T value) => value;
            private static void CopyBackRef<T>(ref T target, object value) => target = (T)value;
        }

        public static void Initialize()
        {
            ConfigureMonoModCecilBackend();
            AppDomain.CurrentDomain.SetData(DispatchDataKey, (Func<MethodBase, object, object[], object>)Dispatch);
            AppDomain.CurrentDomain.SetData(AddOnDataKey, (Action<MethodBase, Delegate, object>)AddOn);
            AppDomain.CurrentDomain.SetData(RemoveOnDataKey, (Action<MethodBase, Delegate>)RemoveOn);
            AppDomain.CurrentDomain.SetData(AddReflectedOnDataKey, (Action<MethodBase, MethodInfo, object, object, object>)AddOnReflected);
            AppDomain.CurrentDomain.SetData(RemoveReflectedOnDataKey, (Action<MethodBase, object>)RemoveOnReflected);
            AppDomain.CurrentDomain.SetData(CreateOriginalDataKey, (Func<MethodBase, Type, Delegate>)CreateOriginalDelegate);
            AppDomain.CurrentDomain.SetData(WeaveAssemblyDataKey, (Func<string, byte[], byte[]>)WeaveAssembly);
            AppDomain.CurrentDomain.SetData(RegisterAssemblyDataKey, (Action<Assembly, byte[]>)IOSILHookRuntime.RegisterAssemblySnapshot);
            AppDomain.CurrentDomain.SetData(AddDetourDataKey, (Action<MethodBase, MethodInfo, object, object>)AddDetour);
            AppDomain.CurrentDomain.SetData(RemoveDetourDataKey, (Action<MethodBase, object>)RemoveDetour);
            AppDomain.CurrentDomain.SetData(DetourIndexDataKey, (Func<MethodBase, object, int>)GetDetourIndex);
            AppDomain.CurrentDomain.SetData(DetourCountDataKey, (Func<MethodBase, int>)GetDetourCount);
            AppDomain.CurrentDomain.SetData(CreateDetourTrampolineDataKey, (Func<MethodBase, object, Type, Delegate>)CreateDetourTrampoline);
            AppDomain.CurrentDomain.SetData(ILHookCountDataKey, (Func<MethodBase, int>)IOSILHookRuntime.GetHookCount);
            AppDomain.CurrentDomain.SetData(ClearDataKey, (Action)Clear);
            IOSILHookRuntime.Initialize();
        }

        private static void ConfigureMonoModCecilBackend()
        {
            Type switches = Type.GetType("MonoMod.Switches, MonoMod.Utils", throwOnError: true);
            MethodInfo setSwitch = switches.GetMethod("SetSwitchValue", BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(string), typeof(object) }, null);
            if (setSwitch == null)
                throw new MissingMethodException(switches.FullName, "SetSwitchValue(string, object)");

            setSwitch.Invoke(null, new object[] { "DMDType", "cecil" });
            Console.WriteLine("iOS hook backend configured MonoMod DynamicMethodDefinition to use its Cecil backend.");
        }

        internal static void ConfigureGameAssembly(string path)
        {
            IOSILHookRuntime.ConfigureGameAssembly(path);
        }

        public static void AddOn(MethodBase target, Delegate hook)
        {
            AddOn(target, hook, null);
        }

        public static void AddOn(MethodBase target, Delegate hook, object config)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (hook == null) throw new ArgumentNullException(nameof(hook));
            ValidateHookSignature(target, hook.Method);

            lock (Sync)
            {
                Registration[] chain;
                if (!OnHooks.TryGetValue(target, out chain)) chain = Array.Empty<Registration>();
                if (chain.Any(item => ReferenceEquals(item.Identity, hook))) return;
                Registration registration = ReadRegistration(hook.Method, hook.Target, hook, config);
                Registration[] next = chain.Concat(new[] { registration }).OrderBy(item => item.Sequence).ToArray();
                OnHooks[target] = OrderByConfiguration(next);
                SetGateEnabled(target, true);
            }
        }

        public static void RemoveOn(MethodBase target, Delegate hook)
        {
            if (target == null || hook == null) return;
            lock (Sync)
            {
                Registration[] chain;
                if (!OnHooks.TryGetValue(target, out chain)) return;
                Registration[] next = chain.Where(item => !ReferenceEquals(item.Identity, hook)).ToArray();
                if (next.Length == 0) OnHooks.Remove(target);
                else OnHooks[target] = next;
                SetGateEnabled(target, next.Length > 0 || ModifiedBodies.ContainsKey(target));
            }
        }

        public static void AddOnReflected(MethodBase target, MethodInfo hookMethod, object hookTarget, object identity, object config)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (hookMethod == null) throw new ArgumentNullException(nameof(hookMethod));
            if (identity == null) throw new ArgumentNullException(nameof(identity));
            if (!hookMethod.IsStatic && hookTarget == null)
                throw new InvalidOperationException("An instance hook method requires a hook target.");
            ValidateHookSignature(target, hookMethod);

            lock (Sync)
            {
                Registration[] chain;
                if (!OnHooks.TryGetValue(target, out chain)) chain = Array.Empty<Registration>();
                if (chain.Any(item => ReferenceEquals(item.Identity, identity))) return;
                Registration registration = ReadRegistration(hookMethod, hookTarget, identity, config);
                Registration[] next = chain.Concat(new[] { registration }).ToArray();
                OnHooks[target] = OrderByConfiguration(next);
                SetGateEnabled(target, true);
            }
        }

        public static void RemoveOnReflected(MethodBase target, object identity)
        {
            if (target == null || identity == null) return;
            lock (Sync)
            {
                Registration[] chain;
                if (!OnHooks.TryGetValue(target, out chain)) return;
                Registration[] next = chain.Where(item => !ReferenceEquals(item.Identity, identity)).ToArray();
                if (next.Length == 0) OnHooks.Remove(target);
                else OnHooks[target] = next;
                SetGateEnabled(target, next.Length > 0 || ModifiedBodies.ContainsKey(target));
            }
        }

        public static void AddDetour(MethodBase target, MethodInfo replacement, object identity, object config)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (replacement == null) throw new ArgumentNullException(nameof(replacement));
            if (identity == null) throw new ArgumentNullException(nameof(identity));

            bool bindsOriginalInstance = !replacement.IsStatic && !target.IsStatic;
            ValidateDetourSignature(target, replacement, bindsOriginalInstance);
            lock (Sync)
            {
                Registration[] chain;
                if (!OnHooks.TryGetValue(target, out chain)) chain = Array.Empty<Registration>();
                if (chain.Any(item => ReferenceEquals(item.Identity, identity))) return;
                Registration registration = ReadRegistration(replacement, null, identity, config);
                registration.UseOriginalInstance = bindsOriginalInstance;
                OnHooks[target] = OrderByConfiguration(chain.Concat(new[] { registration }).ToArray());
                SetGateEnabled(target, true);
            }
        }

        public static void RemoveDetour(MethodBase target, object identity)
        {
            RemoveOnReflected(target, identity);
        }

        public static int GetDetourIndex(MethodBase target, object identity)
        {
            if (target == null || identity == null) return -1;
            lock (Sync)
            {
                Registration[] chain;
                if (!OnHooks.TryGetValue(target, out chain)) return -1;
                for (int i = 0; i < chain.Length; i++)
                    if (ReferenceEquals(chain[i].Identity, identity)) return i;
                return chain.Length;
            }
        }

        public static int GetDetourCount(MethodBase target)
        {
            if (target == null) return 0;
            lock (Sync)
            {
                Registration[] chain;
                return OnHooks.TryGetValue(target, out chain) ? chain.Length : 0;
            }
        }

        public static Delegate CreateDetourTrampoline(MethodBase target, object identity, Type delegateType)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (identity == null) throw new ArgumentNullException(nameof(identity));
            if (delegateType == null) throw new ArgumentNullException(nameof(delegateType));
            Registration[] chain;
            int index;
            lock (Sync)
            {
                if (!OnHooks.TryGetValue(target, out chain)) chain = Array.Empty<Registration>();
                index = Array.FindIndex(chain, item => ReferenceEquals(item.Identity, identity));
            }
            if (index < 0) index = -1;
            MethodInfo original = OriginalMethods.GetOrAdd(target, FindOriginalMethod);
            return new Invocation(target, original, chain, null).CreateContinuation(delegateType, index + 1);
        }

        public static void Clear()
        {
            lock (Sync)
            {
                foreach (MethodBase target in OnHooks.Keys.ToArray())
                    SetGateEnabled(target, ModifiedBodies.ContainsKey(target));
                OnHooks.Clear();
            }
            IOSILHookRuntime.Clear();
        }

        internal static void SetModifiedBody(MethodBase target, MethodInfo body)
        {
            lock (Sync)
            {
                if (body == null) ModifiedBodies.Remove(target);
                else ModifiedBodies[target] = body;
                Registration[] hooks;
                bool hasOnHooks = OnHooks.TryGetValue(target, out hooks) && hooks.Length > 0;
                SetGateEnabled(target, hasOnHooks || body != null);
            }
        }

        private static MethodInfo GetModifiedBody(MethodBase target)
        {
            lock (Sync)
            {
                MethodInfo body;
                return ModifiedBodies.TryGetValue(target, out body) ? body : null;
            }
        }

        private static void SetGateEnabled(MethodBase target, bool enabled)
        {
            string fieldName = IOSHookIdentity.GateFieldName(target);
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            FieldInfo field = target.DeclaringType.GetField(fieldName, flags);
            if (field == null)
            {
                if (enabled)
                    throw new MissingFieldException("The iOS hook weave did not provide gate " + fieldName + " for " + target + ".");
                return;
            }
            field.SetValue(null, enabled);
        }

        private static Registration ReadRegistration(MethodInfo method, object hookTarget, object identity, object config)
        {
            string id = ReadConfigValue(config, "Id", "ID") as string;
            if (string.IsNullOrWhiteSpace(id))
                id = (method.DeclaringType == null ? "" : method.DeclaringType.FullName + ".") + method.Name;

            object priorityValue = ReadConfigValue(config, "Priority");
            int? priority = priorityValue == null ? (int?)null : Convert.ToInt32(priorityValue);
            object subPriorityValue = ReadConfigValue(config, "SubPriority");
            int subPriority = subPriorityValue == null ? 0 : Convert.ToInt32(subPriorityValue);

            return new Registration
            {
                Hook = identity as Delegate,
                HookMethod = method,
                HookTarget = hookTarget,
                Identity = identity,
                Sequence = Interlocked.Increment(ref nextSequence),
                Id = id,
                Priority = priority,
                SubPriority = subPriority,
                Before = ReadStringArray(ReadConfigValue(config, "Before")),
                After = ReadStringArray(ReadConfigValue(config, "After"))
            };
        }

        private static object ReadConfigValue(object config, params string[] names)
        {
            if (config == null) return null;
            Type type = config.GetType();
            foreach (string name in names)
            {
                PropertyInfo property = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
                if (property != null && property.GetIndexParameters().Length == 0)
                    return property.GetValue(config, null);
                FieldInfo field = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
                if (field != null) return field.GetValue(config);
            }
            return null;
        }

        private static string[] ReadStringArray(object value)
        {
            if (value == null || value is string) return Array.Empty<string>();
            IEnumerable values = value as IEnumerable;
            if (values == null) return Array.Empty<string>();
            return values.Cast<object>().Where(item => item != null).Select(item => item.ToString()).ToArray();
        }

        private static Registration[] OrderByConfiguration(Registration[] registrations)
        {
            Registration[] baseline = registrations
                .OrderBy(item => item.Priority.HasValue ? 0 : 1)
                .ThenByDescending(item => item.Priority)
                .ThenByDescending(item => item.SubPriority)
                .ThenBy(item => item.Sequence)
                .ToArray();

            Dictionary<Registration, HashSet<Registration>> before = baseline.ToDictionary(item => item, item => new HashSet<Registration>());
            for (int i = 0; i < baseline.Length; i++)
            {
                Registration item = baseline[i];
                for (int j = 0; j < baseline.Length; j++)
                {
                    if (i == j) continue;
                    Registration other = baseline[j];
                    if (item.Before.Contains(other.Id, StringComparer.Ordinal)) before[other].Add(item);
                    if (item.After.Contains(other.Id, StringComparer.Ordinal)) before[item].Add(other);
                    if (other.Before.Contains(item.Id, StringComparer.Ordinal)) before[item].Add(other);
                    if (other.After.Contains(item.Id, StringComparer.Ordinal)) before[other].Add(item);
                }
            }

            List<Registration> ordered = new List<Registration>(baseline.Length);
            HashSet<Registration> visiting = new HashSet<Registration>();
            HashSet<Registration> visited = new HashSet<Registration>();
            foreach (Registration registration in baseline) Visit(registration);
            return ordered.ToArray();

            void Visit(Registration registration)
            {
                if (visited.Contains(registration)) return;
                if (!visiting.Add(registration))
                    throw new InvalidOperationException("The iOS hook ordering constraints contain a cycle at '" + registration.Id + "'.");
                foreach (Registration predecessor in baseline.Where(before[registration].Contains)) Visit(predecessor);
                visiting.Remove(registration);
                visited.Add(registration);
                ordered.Add(registration);
            }
        }

        public static object Dispatch(MethodBase target, object instance, object[] arguments)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (arguments == null) arguments = Array.Empty<object>();

            bool valueTypeInstance = !target.IsStatic && target.DeclaringType.IsValueType;
            object[] targetArguments = arguments;
            if (valueTypeInstance)
            {
                if (arguments.Length != target.GetParameters().Length + 1)
                    throw new InvalidOperationException("A value-type gate did not provide its instance value for " + target + ".");
                instance = arguments[0];
                targetArguments = new object[arguments.Length - 1];
                Array.Copy(arguments, 1, targetArguments, 0, targetArguments.Length);
            }

            Registration[] registrations;
            lock (Sync)
                if (!OnHooks.TryGetValue(target, out registrations))
                    registrations = Array.Empty<Registration>();

            MethodInfo original = OriginalMethods.GetOrAdd(target, FindOriginalMethod);
            Invocation invocation = new Invocation(target, original, registrations, instance);
            object result = invocation.InvokeNext(0, instance, targetArguments);
            if (valueTypeInstance)
            {
                arguments[0] = invocation.CurrentInstance;
                Array.Copy(targetArguments, 0, arguments, 1, targetArguments.Length);
            }
            return result;
        }

        private static MethodInfo FindOriginalMethod(MethodBase target)
        {
            MethodInfo method = target as MethodInfo;
            bool constructor = target is ConstructorInfo;
            if (method == null && !constructor)
                throw new NotSupportedException("The iOS gate cannot resolve an original body for " + target + ".");

            string name = IOSHookIdentity.OriginalMethodName(target);
            bool valueTypeInstance = !target.IsStatic && target.DeclaringType.IsValueType;
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                (target.IsStatic || valueTypeInstance ? BindingFlags.Static : BindingFlags.Instance);
            if (valueTypeInstance)
            {
                MethodInfo invoker = target.DeclaringType.GetMethod(IOSHookIdentity.ValueTypeInvokerName(target), flags);
                if (invoker == null)
                    throw new MissingMethodException("The iOS hook weave did not provide the value-type original invoker for " + target + ".");
                if (method != null && method.IsGenericMethod && !method.IsGenericMethodDefinition)
                    invoker = invoker.GetGenericMethodDefinition().MakeGenericMethod(method.GetGenericArguments());
                return invoker;
            }
            MethodInfo original = target.DeclaringType.GetMethod(name, flags);
            if (original == null)
                throw new MissingMethodException("The iOS hook weave did not provide the original body for " + target + ".");

            if (method != null && method.IsGenericMethod && !method.IsGenericMethodDefinition)
                original = original.GetGenericMethodDefinition().MakeGenericMethod(method.GetGenericArguments());
            return original;
        }

        private static void ValidateHookSignature(MethodBase target, MethodInfo hookMethod)
        {
            ParameterInfo[] targetParameters = target.GetParameters();
            ParameterInfo[] hookParameters = hookMethod.GetParameters();
            int targetArgumentCount = targetParameters.Length + (target.IsStatic ? 0 : 1);
            bool hasOrig = hookParameters.Length == targetArgumentCount + 1 &&
                hookParameters.Length > 0 && typeof(Delegate).IsAssignableFrom(hookParameters[0].ParameterType);
            int expected = targetArgumentCount + (hasOrig ? 1 : 0);
            if (hookParameters.Length != expected)
                throw new InvalidOperationException("The hook signature does not match " + target + ".");

            if (hasOrig)
            {
                MethodInfo invoke = hookParameters[0].ParameterType.GetMethod("Invoke");
                if (invoke == null || invoke.ReturnType != ((target as MethodInfo)?.ReturnType ?? typeof(void)))
                    throw new InvalidOperationException("The orig delegate return type does not match " + target + ".");
                if (invoke.GetParameters().Length != targetArgumentCount)
                    throw new InvalidOperationException("The orig delegate parameter count does not match " + target + ".");
            }

            Type targetReturn = (target as MethodInfo)?.ReturnType ?? typeof(void);
            if (hookMethod.ReturnType != targetReturn)
                throw new InvalidOperationException("The hook return type does not match " + target + ".");
        }

        private static void ValidateDetourSignature(MethodBase target, MethodInfo replacement, bool bindsOriginalInstance)
        {
            if (target is ConstructorInfo)
                throw new NotSupportedException("Constructor detours require an explicit iOS constructor gate: " + target);
            if (target.IsStatic && !replacement.IsStatic)
                throw new InvalidOperationException("A static method cannot be used as the instance target of a detour on " + target + ".");
            ParameterInfo[] targetParameters = target.GetParameters();
            ParameterInfo[] replacementParameters = replacement.GetParameters();
            if (replacementParameters.Length != targetParameters.Length +
                (target.IsStatic || bindsOriginalInstance ? 0 : 1))
                throw new InvalidOperationException("The detour signature does not match " + target + ".");
            Type targetReturn = ((MethodInfo)target).ReturnType;
            if (replacement.ReturnType != targetReturn)
                throw new InvalidOperationException("The detour return type does not match " + target + ".");
            int start = bindsOriginalInstance || target.IsStatic ? 0 : 1;
            if (start == 1)
            {
                Type expectedInstance = target.DeclaringType.IsValueType
                    ? target.DeclaringType.MakeByRefType()
                    : target.DeclaringType;
                if (replacementParameters[0].ParameterType != expectedInstance)
                    throw new InvalidOperationException("The detour instance parameter does not match " + target + ".");
            }
            for (int i = 0; i < targetParameters.Length; i++)
                if (replacementParameters[i + start].ParameterType != targetParameters[i].ParameterType)
                    throw new InvalidOperationException("Detour parameter " + i + " does not match " + target + ".");
        }

        internal static Delegate CreateOriginalDelegate(MethodBase target, Type delegateType)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (delegateType == null) throw new ArgumentNullException(nameof(delegateType));
            MethodInfo original = OriginalMethods.GetOrAdd(target, FindOriginalMethod);
            return new Invocation(target, original, Array.Empty<Registration>(), null)
                .CreateContinuation(delegateType, 0);
        }

        internal static byte[] WeaveAssembly(string assemblyPath, byte[] bytes)
        {
            string assemblyName;
            int woven;
            string[] skipped;
            string resolverDirectory = string.IsNullOrWhiteSpace(assemblyPath) ? null : Path.GetDirectoryName(assemblyPath);
            byte[] result = global::IOSHookWeaver.WeaveAssembly(bytes, out assemblyName, out woven, out skipped, resolverDirectory);
            if (woven > 0)
                Console.WriteLine("iOS hook backend wove " + woven + " methods in mod assembly " + assemblyName + ".");
            if (skipped.Length > 0)
                Console.WriteLine("iOS hook backend left " + skipped.Length + " unsupported method bodies unhookable in " + assemblyName + ".");
            return result;
        }
    }
}
