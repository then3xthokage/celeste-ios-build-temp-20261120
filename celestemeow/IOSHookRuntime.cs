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
using System.Runtime.Loader;
using System.Runtime.CompilerServices;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace celestemeow
{
    public sealed class IOSTypedHookInvocationState
    {
        public Delegate Hook;
        public Delegate Original;
    }

    public sealed class IOSTypedContinuationState
    {
        public Delegate[] Chain;
        public Delegate Original;
        public int Index;
    }

    public sealed class IOSTypedDetourInvocationState
    {
        public MethodInfo Method;
        public Type DelegateType;
        private readonly ConditionalWeakTable<object, Delegate> delegates = new ConditionalWeakTable<object, Delegate>();

        public Delegate GetBoundDelegate(object target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            return delegates.GetValue(target, instance => Delegate.CreateDelegate(DelegateType, instance, Method, true));
        }
    }

    public sealed class IOSTypedDetourTrampolineState
    {
        public MethodBase Target;
        public object Identity;
        public Type DelegateType;
        private Delegate continuation;

        public Delegate Continuation
        {
            get => Volatile.Read(ref continuation);
            set => Volatile.Write(ref continuation, value);
        }
    }

    public static class IOSTypedHookBridge
    {
        public static Delegate GetNext(IOSTypedContinuationState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            return state.Index < state.Chain.Length ? state.Chain[state.Index] : state.Original;
        }

        public static Delegate GetBoundDetour(IOSTypedDetourInvocationState state, object target)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            return state.GetBoundDelegate(target);
        }

        public static Delegate GetDetourContinuation(IOSTypedDetourTrampolineState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            return state.Continuation ?? throw new InvalidOperationException("The managed detour continuation is not initialized.");
        }
    }

    /// <summary>
    /// Managed dispatch core for the iOS hook gates. The gates themselves are
    /// woven into Celeste.dll at build time, so this path never writes executable
    /// memory or relies on Reflection.Emit.
    /// </summary>
    internal static class IOSHookRuntime
    {
        internal const string DispatchDataKey = "Celeste.IOSHookRuntime.Dispatch";
        internal const string TypedDispatchDataKey = "Celeste.IOSHookRuntime.DispatchTyped";
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
        internal const string CreateDetourTrampolineMethodDataKey = "Celeste.IOSHookRuntime.CreateDetourTrampolineMethod";
        internal const string ILHookCountDataKey = "Celeste.IOSHookRuntime.ILHookCount";
        internal const string ClearDataKey = "Celeste.IOSHookRuntime.Clear";

        private static readonly object Sync = new object();
        private static readonly Dictionary<MethodBase, Registration[]> OnHooks = new Dictionary<MethodBase, Registration[]>();
        private static readonly Dictionary<MethodBase, List<IOSTypedDetourTrampolineState>> DetourTrampolineStates = new Dictionary<MethodBase, List<IOSTypedDetourTrampolineState>>();
        private static readonly Dictionary<MethodBase, MethodInfo> ModifiedBodies = new Dictionary<MethodBase, MethodInfo>();
        private static readonly ConcurrentDictionary<MethodBase, MethodInfo> OriginalMethods = new ConcurrentDictionary<MethodBase, MethodInfo>();
        private static readonly Dictionary<MethodBase, Dictionary<Type, Delegate>> TypedDispatchCache = new Dictionary<MethodBase, Dictionary<Type, Delegate>>();
        private static readonly ConditionalWeakTable<AssemblyLoadContext, TypedAssemblyResolver> TypedResolvers = new ConditionalWeakTable<AssemblyLoadContext, TypedAssemblyResolver>();
        private static long nextSequence;
        private static string gameAssemblyPath;

        private sealed class Registration
        {
            public Delegate Hook;
            public MethodInfo HookMethod;
            public object HookTarget;
            public object Identity;
            public bool IsDetour;
            public bool UseOriginalInstance;
            public long Sequence;
            public string Id;
            public int? Priority;
            public int SubPriority;
            public string[] Before;
            public string[] After;
        }

        private sealed class TypedAssemblyResolver
        {
            private readonly object sync = new object();
            private readonly List<Assembly> preferredAssemblies = new List<Assembly>();

            public void Add(Assembly assembly)
            {
                if (assembly == null) return;
                lock (sync)
                    if (!preferredAssemblies.Contains(assembly)) preferredAssemblies.Add(assembly);
            }

            public Assembly Resolve(AssemblyLoadContext context, AssemblyName requested)
            {
                Assembly[] preferred;
                lock (sync) preferred = preferredAssemblies.ToArray();
                return ResolveLoadedAssembly(requested.FullName, preferred);
            }
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
                MethodInfo hookMethod = CloseHookMethodForTarget(registration.HookMethod, target,
                    registration.IsDetour, registration.UseOriginalInstance);
                ParameterInfo[] hookParameters = hookMethod.GetParameters();
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
                        : hookMethod.Invoke(instanceBound ? instance : registration.HookTarget, hookArguments);
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
            AppDomain.CurrentDomain.SetData(DispatchDataKey, (Func<MethodBase, object, object[], object>)Dispatch);
            AppDomain.CurrentDomain.SetData(TypedDispatchDataKey, (Func<MethodBase, Type, Delegate>)DispatchTyped);
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
            AppDomain.CurrentDomain.SetData(CreateDetourTrampolineMethodDataKey, (Func<MethodBase, object, MethodBase, MethodBase>)CreateDetourTrampolineMethod);
            AppDomain.CurrentDomain.SetData(ILHookCountDataKey, (Func<MethodBase, int>)IOSILHookRuntime.GetHookCount);
            AppDomain.CurrentDomain.SetData(ClearDataKey, (Action)Clear);
            IOSILHookRuntime.Initialize();
            ConfigureMonoModCecilBackend();
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
            gameAssemblyPath = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
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
            if (target.ContainsGenericParameters)
                throw new NotSupportedException("A delegate-based On hook needs a closed target; use a reflected generic hook method for " + target + ".");
            ValidateHookSignature(target, hook.Method);

            lock (Sync)
            {
                Registration[] chain;
                if (!OnHooks.TryGetValue(target, out chain)) chain = Array.Empty<Registration>();
                if (chain.Any(item => ReferenceEquals(item.Identity, hook))) return;
                Registration registration = ReadRegistration(hook.Method, hook.Target, hook, config);
                Registration[] next = chain.Concat(new[] { registration }).OrderBy(item => item.Sequence).ToArray();
                OnHooks[target] = OrderByConfiguration(next);
                TypedDispatchCache.Remove(target);
                SetGateEnabled(target, true);
                RefreshDetourTrampolinesLocked(target);
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
                TypedDispatchCache.Remove(target);
                SetGateEnabled(target, HasGateSource(target));
                RefreshDetourTrampolinesLocked(target);
            }
        }

        public static void AddOnReflected(MethodBase target, MethodInfo hookMethod, object hookTarget, object identity, object config)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (hookMethod == null) throw new ArgumentNullException(nameof(hookMethod));
            if (identity == null) throw new ArgumentNullException(nameof(identity));
            if (!hookMethod.IsStatic && hookTarget == null)
                throw new InvalidOperationException("An instance hook method requires a hook target.");
            CloseHookMethodForTarget(hookMethod, target, false, false);

            lock (Sync)
            {
                Registration[] chain;
                if (!OnHooks.TryGetValue(target, out chain)) chain = Array.Empty<Registration>();
                if (chain.Any(item => ReferenceEquals(item.Identity, identity))) return;
                Registration registration = ReadRegistration(hookMethod, hookTarget, identity, config);
                Registration[] next = chain.Concat(new[] { registration }).ToArray();
                OnHooks[target] = OrderByConfiguration(next);
                InvalidateTypedDispatchForTarget(target);
                SetGateEnabled(target, true);
                RefreshDetourTrampolinesLocked(target);
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
                InvalidateTypedDispatchForTarget(target);
                SetGateEnabled(target, HasGateSource(target));
                RefreshDetourTrampolinesLocked(target);
            }
        }

        public static void AddDetour(MethodBase target, MethodInfo replacement, object identity, object config)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (replacement == null) throw new ArgumentNullException(nameof(replacement));
            if (identity == null) throw new ArgumentNullException(nameof(identity));

            bool bindsOriginalInstance = !replacement.IsStatic && !target.IsStatic;
            CloseHookMethodForTarget(replacement, target, true, bindsOriginalInstance);
            lock (Sync)
            {
                Registration[] chain;
                if (!OnHooks.TryGetValue(target, out chain)) chain = Array.Empty<Registration>();
                if (chain.Any(item => ReferenceEquals(item.Identity, identity))) return;
                Registration registration = ReadRegistration(replacement, null, identity, config);
                registration.IsDetour = true;
                registration.UseOriginalInstance = bindsOriginalInstance;
                OnHooks[target] = OrderByConfiguration(chain.Concat(new[] { registration }).ToArray());
                InvalidateTypedDispatchForTarget(target);
                SetGateEnabled(target, true);
                RefreshDetourTrampolinesLocked(target);
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
                Registration[] chain = GetRegistrationsForTargetLocked(target);
                for (int i = 0; i < chain.Length; i++)
                    if (ReferenceEquals(chain[i].Identity, identity)) return i;
                return chain.Length == 0 ? -1 : chain.Length;
            }
        }

        public static int GetDetourCount(MethodBase target)
        {
            if (target == null) return 0;
            lock (Sync)
                return GetRegistrationsForTargetLocked(target).Length;
        }

        public static Delegate CreateDetourTrampoline(MethodBase target, object identity, Type delegateType)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (identity == null) throw new ArgumentNullException(nameof(identity));
            if (delegateType == null) throw new ArgumentNullException(nameof(delegateType));

            return CreateDetourTrampolineWrapper(target, identity, delegateType).CreateDelegate(delegateType);
        }

        private static MethodInfo CreateDetourTrampolineWrapper(MethodBase target, object identity, Type delegateType)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (identity == null) throw new ArgumentNullException(nameof(identity));
            if (delegateType == null) throw new ArgumentNullException(nameof(delegateType));

            string typedDelegateName = "CelesteIOS.Generated.__ios_typed_delegate_" + IOSHookIdentity.ForMethod(target);
            Type wovenTypedDelegate = target.Module.Assembly.GetType(typedDelegateName, false, false);
            if (wovenTypedDelegate != null && !HaveSameTypedSignature(GetTypedDelegateType(target), delegateType))
                throw new InvalidOperationException("The typed detour trampoline delegate does not match " + target + ".");

            MethodInfo delegateInvoke = delegateType.GetMethod("Invoke")
                ?? throw new MissingMethodException(delegateType.FullName, "Invoke");
            IOSTypedDetourTrampolineState state = new IOSTypedDetourTrampolineState
            {
                Target = target,
                Identity = identity,
                DelegateType = delegateType
            };
            lock (Sync)
                state.Continuation = CreateDetourTrampolineSnapshot(target, identity, delegateType);

            string unique = Interlocked.Increment(ref nextSequence).ToString();
            string assemblyName = "CelesteIOS.LegacyTrampoline." + unique;
            AssemblyDefinition assembly = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition(assemblyName, new Version(1, 0, 0, 0)), assemblyName, ModuleKind.Dll);
            ModuleDefinition module = assembly.MainModule;
            TypeDefinition helper = new TypeDefinition(
                "CelesteIOS.Generated",
                "LegacyTrampoline_" + unique,
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Abstract |
                Mono.Cecil.TypeAttributes.Sealed | Mono.Cecil.TypeAttributes.BeforeFieldInit,
                module.ImportReference(typeof(object)));
            module.Types.Add(helper);

            FieldDefinition stateField = new FieldDefinition(
                "State",
                Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static,
                module.ImportReference(typeof(IOSTypedDetourTrampolineState)));
            helper.Fields.Add(stateField);

            Mono.Cecil.MethodDefinition wrapper = new Mono.Cecil.MethodDefinition(
                "Invoke",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static |
                Mono.Cecil.MethodAttributes.HideBySig,
                ImportRuntimeParameterSignature(module, delegateInvoke.ReturnParameter));
            foreach (ParameterInfo parameter in delegateInvoke.GetParameters())
                wrapper.Parameters.Add(new ParameterDefinition(parameter.Name,
                    (Mono.Cecil.ParameterAttributes)parameter.Attributes,
                    ImportRuntimeParameterSignature(module, parameter)));
            helper.Methods.Add(wrapper);

            ILProcessor il = wrapper.Body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldsfld, stateField));
            MethodInfo getContinuation = typeof(IOSTypedHookBridge).GetMethod(
                nameof(IOSTypedHookBridge.GetDetourContinuation), BindingFlags.Public | BindingFlags.Static);
            il.Append(il.Create(OpCodes.Call, module.ImportReference(getContinuation)));
            il.Append(il.Create(OpCodes.Castclass, module.ImportReference(delegateType)));
            ParameterInfo[] parameters = delegateInvoke.GetParameters();
            for (int index = 0; index < parameters.Length; index++)
                il.Append(LoadCecilArgument(il, index));
            il.Append(il.Create(OpCodes.Callvirt,
                CreateRuntimeInstanceMethodReference(module, delegateType, delegateInvoke)));
            il.Append(il.Create(OpCodes.Ret));

            AssemblyLoadContext loadContext = AssemblyLoadContext.GetLoadContext(target.Module.Assembly) ?? AssemblyLoadContext.Default;
            Assembly loaded = LoadGeneratedAssembly(assembly, loadContext,
                target.Module.Assembly, delegateType.Assembly, typeof(IOSTypedDetourTrampolineState).Assembly);
            Type loadedHelper = loaded.GetType(helper.FullName, true, false);
            loadedHelper.GetField(stateField.Name, BindingFlags.Public | BindingFlags.Static).SetValue(null, state);
            lock (Sync)
            {
                if (!DetourTrampolineStates.TryGetValue(target, out List<IOSTypedDetourTrampolineState> states))
                    DetourTrampolineStates[target] = states = new List<IOSTypedDetourTrampolineState>();
                states.Add(state);
                state.Continuation = CreateDetourTrampolineSnapshot(target, identity, delegateType);
            }
            return loadedHelper.GetMethod(wrapper.Name, BindingFlags.Public | BindingFlags.Static);
        }

        private static Delegate CreateDetourTrampolineSnapshot(MethodBase target, object identity, Type delegateType)
        {
            Registration[] chain;
            int index;
            chain = GetRegistrationsForTargetLocked(target);
            index = Array.FindIndex(chain, item => ReferenceEquals(item.Identity, identity));
            string typedDelegateName = "CelesteIOS.Generated.__ios_typed_delegate_" + IOSHookIdentity.ForMethod(target);
            Type wovenTypedDelegate = target.Module.Assembly.GetType(typedDelegateName, false, false);
            if (wovenTypedDelegate != null)
            {
                Type expectedDelegate = GetTypedDelegateType(target);
                Delegate continuation = CreateTypedPipelineContinuation(target, expectedDelegate, chain, Math.Max(index + 1, 0));
                return AdaptTypedDelegate(continuation, delegateType);
            }
            if (index < 0) index = -1;
            MethodInfo original = OriginalMethods.GetOrAdd(target, FindOriginalMethod);
            return new Invocation(target, original, chain, null).CreateContinuation(delegateType, index + 1);
        }

        public static MethodBase CreateDetourTrampolineMethod(MethodBase target, object identity, MethodBase signature)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (identity == null) throw new ArgumentNullException(nameof(identity));

            string typedDelegateName = "CelesteIOS.Generated.__ios_typed_delegate_" + IOSHookIdentity.ForMethod(target);
            Type wovenDelegateType = target.Module.Assembly.GetType(typedDelegateName, false, false);
            Type delegateType;
            if (wovenDelegateType != null)
            {
                delegateType = GetTypedDelegateType(target);
            }
            else
            {
                MethodBase fallbackSignature = signature ?? GetLegacyTrampolineDefaultSignature(identity, target);
                if (!(fallbackSignature is MethodInfo fallbackMethod))
                    throw new NotSupportedException("A legacy iOS trampoline needs a method-shaped signature for " + target + ".");
                delegateType = CreateDelegateTypeForMethod(fallbackMethod);
            }
            MethodInfo delegateInvoke = delegateType.GetMethod("Invoke")
                ?? throw new MissingMethodException(delegateType.FullName, "Invoke");
            if (signature != null)
                ValidateDetourTrampolineSignature(target, signature, delegateInvoke);

            return CreateDetourTrampolineWrapper(target, identity, delegateType);
        }

        private static MethodBase GetLegacyTrampolineDefaultSignature(object identity, MethodBase target)
        {
            MethodInfo callback = identity?.GetType().GetField("Target", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(identity) as MethodInfo;
            if (callback == null) return target;

            string typeName = identity.GetType().FullName ?? string.Empty;
            if (typeName.StartsWith("Celeste.Mod.Helpers.LegacyMonoMod.LegacyHook", StringComparison.Ordinal))
            {
                ParameterInfo[] parameters = callback.GetParameters();
                if (parameters.Length > 0 && typeof(Delegate).IsAssignableFrom(parameters[0].ParameterType))
                    return parameters[0].ParameterType.GetMethod("Invoke");
            }
            return callback;
        }

        private static void ValidateDetourTrampolineSignature(MethodBase target, MethodBase signature, MethodInfo delegateInvoke)
        {
            Type requestedReturn = signature is MethodInfo method ? method.ReturnType : typeof(void);
            if (requestedReturn != delegateInvoke.ReturnType)
                throw new InvalidOperationException("The requested iOS legacy trampoline return type does not match " + target + ".");
            if (signature is MethodInfo requestedMethod &&
                !SameCustomModifiers(delegateInvoke.ReturnParameter, requestedMethod.ReturnParameter))
                throw new InvalidOperationException("The requested iOS legacy trampoline return modifiers do not match " + target + ".");

            ParameterInfo[] requestedParameters = signature.GetParameters();
            ParameterInfo[] expectedParameters = delegateInvoke.GetParameters();
            if (requestedParameters.Length != expectedParameters.Length)
                throw new InvalidOperationException("The requested iOS legacy trampoline parameter count does not match " + target + ".");
            for (int index = 0; index < requestedParameters.Length; index++)
                if (requestedParameters[index].ParameterType != expectedParameters[index].ParameterType ||
                    !SameCustomModifiers(requestedParameters[index], expectedParameters[index]))
                    throw new InvalidOperationException("The requested iOS legacy trampoline parameter " + index + " does not match " + target + ".");
        }

        private static void RefreshDetourTrampolinesLocked(MethodBase target)
        {
            if (!DetourTrampolineStates.TryGetValue(target, out List<IOSTypedDetourTrampolineState> states))
                return;
            foreach (IOSTypedDetourTrampolineState state in states)
                state.Continuation = CreateDetourTrampolineSnapshot(state.Target, state.Identity, state.DelegateType);
        }

        private static void RefreshAllDetourTrampolinesLocked()
        {
            foreach (MethodBase target in DetourTrampolineStates.Keys.ToArray())
                RefreshDetourTrampolinesLocked(target);
        }

        public static void Clear()
        {
            lock (Sync)
            {
                MethodBase[] targets = OnHooks.Keys.ToArray();
                OnHooks.Clear();
                TypedDispatchCache.Clear();
                foreach (MethodBase target in targets)
                    SetGateEnabled(target, HasGateSource(target));
                RefreshAllDetourTrampolinesLocked();
            }
            IOSILHookRuntime.Clear();
        }

        internal static void SetModifiedBody(MethodBase target, MethodInfo body)
        {
            lock (Sync)
            {
                if (body == null) ModifiedBodies.Remove(target);
                else ModifiedBodies[target] = body;
                foreach (MethodBase cachedTarget in TypedDispatchCache.Keys.ToArray())
                {
                    if (cachedTarget.Equals(target))
                    {
                        TypedDispatchCache.Remove(cachedTarget);
                        continue;
                    }
                    try
                    {
                        if (IOSHookIdentity.GetMethodDefinitionPreservingDeclaringType(cachedTarget).Equals(target) ||
                            IOSHookIdentity.GetDefinition(cachedTarget).Equals(target))
                            TypedDispatchCache.Remove(cachedTarget);
                    }
                    catch { }
                }
                foreach (MethodBase trampolineTarget in DetourTrampolineStates.Keys.ToArray())
                {
                    bool matches = trampolineTarget.Equals(target);
                    try
                    {
                        matches |= IOSHookIdentity.GetMethodDefinitionPreservingDeclaringType(trampolineTarget).Equals(target) ||
                            IOSHookIdentity.GetDefinition(trampolineTarget).Equals(target);
                    }
                    catch { }
                    if (matches) RefreshDetourTrampolinesLocked(trampolineTarget);
                }
                SetGateEnabled(target, HasGateSource(target));
            }
        }

        private static MethodInfo GetModifiedBody(MethodBase target)
        {
            MethodInfo body;
            lock (Sync)
            {
                if (!ModifiedBodies.TryGetValue(target, out body))
                {
                    MethodBase sameDeclaringTypeDefinition = IOSHookIdentity.GetMethodDefinitionPreservingDeclaringType(target);
                    if (sameDeclaringTypeDefinition.Equals(target) || !ModifiedBodies.TryGetValue(sameDeclaringTypeDefinition, out body))
                    {
                        MethodBase openGenericTarget = IOSHookIdentity.GetDefinition(target);
                        if (openGenericTarget.Equals(target) || !ModifiedBodies.TryGetValue(openGenericTarget, out body))
                            return null;
                    }
                }
            }
            return CloseModifiedBodyForTarget(body, target);
        }

        private static MethodInfo CloseModifiedBodyForTarget(MethodInfo body, MethodBase target)
        {
            MethodInfo targetMethod = target as MethodInfo;
            Type targetDeclaringType = target.DeclaringType;
            if (body.DeclaringType.IsGenericTypeDefinition && targetDeclaringType.IsConstructedGenericType)
            {
                Type closedBodyType = body.DeclaringType.MakeGenericType(targetDeclaringType.GetGenericArguments());
                body = closedBodyType.GetMethod(body.Name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (body == null) throw new MissingMethodException("The iOS IL sidecar lost its generated body for " + target + ".");
            }
            if (body.IsGenericMethodDefinition && targetMethod != null &&
                targetMethod.IsGenericMethod && !targetMethod.IsGenericMethodDefinition)
                body = body.MakeGenericMethod(targetMethod.GetGenericArguments());
            if (body.ContainsGenericParameters && !target.ContainsGenericParameters)
                throw new InvalidOperationException("The modified iOS IL body could not close its generic parameters for " + target + ".");
            return body;
        }

        private static void SetGateEnabled(MethodBase target, bool enabled)
        {
            if (target is ConstructorInfo staticConstructor && staticConstructor.IsStatic && !IsModuleInitializer(target))
                return;
            if (IsModuleInitializer(target))
            {
                Type container = target.Module.Assembly.GetType(IOSHookIdentity.ModuleOriginalContainerName(target), false, false);
                FieldInfo moduleGate = container?.GetField(IOSHookIdentity.GateFieldName(target),
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (moduleGate == null)
                {
                    if (enabled)
                        throw new MissingFieldException("The iOS hook weave did not provide a module-level gate for " + target + ".");
                    return;
                }
                moduleGate.SetValue(null, enabled);
                return;
            }
            if (target.DeclaringType.ContainsGenericParameters)
            {
                Type stateType = target.Module.Assembly.GetType(IOSHookIdentity.GenericGateStateTypeFullName, false, false);
                FieldInfo genericGate = stateType?.GetField(IOSHookIdentity.GenericOpenGateFieldName(target),
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (genericGate == null)
                {
                    if (enabled)
                        throw new MissingFieldException("The iOS hook weave did not provide a shared generic gate for " + target + ".");
                    return;
                }
                genericGate.SetValue(null, enabled);
                return;
            }
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

        private static Registration[] GetRegistrationsForTargetLocked(MethodBase target)
        {
            List<Registration> result = new List<Registration>();
            AddRegistrationsForKey(target, result);

            MethodBase sameDeclaringTypeDefinition = IOSHookIdentity.GetMethodDefinitionPreservingDeclaringType(target);
            if (!sameDeclaringTypeDefinition.Equals(target))
                AddRegistrationsForKey(sameDeclaringTypeDefinition, result);

            MethodBase openGenericDefinition = IOSHookIdentity.GetDefinition(target);
            if (!openGenericDefinition.Equals(target) && !openGenericDefinition.Equals(sameDeclaringTypeDefinition))
                AddRegistrationsForKey(openGenericDefinition, result);

            return OrderByConfiguration(result.ToArray());
        }

        private static void AddRegistrationsForKey(MethodBase key, List<Registration> result)
        {
            Registration[] registrations;
            if (!OnHooks.TryGetValue(key, out registrations)) return;
            foreach (Registration registration in registrations)
                if (!result.Any(existing => ReferenceEquals(existing.Identity, registration.Identity)))
                    result.Add(registration);
        }

        private static void InvalidateTypedDispatchForTarget(MethodBase target)
        {
            MethodBase openGenericDefinition = IOSHookIdentity.GetDefinition(target);
            foreach (MethodBase cachedTarget in TypedDispatchCache.Keys.ToArray())
            {
                if (cachedTarget.Equals(target) ||
                    IOSHookIdentity.GetMethodDefinitionPreservingDeclaringType(cachedTarget).Equals(target) ||
                    IOSHookIdentity.GetDefinition(cachedTarget).Equals(target) ||
                    IOSHookIdentity.GetDefinition(cachedTarget).Equals(openGenericDefinition))
                    TypedDispatchCache.Remove(cachedTarget);
            }
        }

        private static bool HasGateSource(MethodBase target)
        {
            foreach (KeyValuePair<MethodBase, Registration[]> entry in OnHooks)
                if (entry.Value.Length > 0 && SameGateFamily(entry.Key, target))
                    return true;
            foreach (MethodBase modifiedTarget in ModifiedBodies.Keys)
                if (SameGateFamily(modifiedTarget, target))
                    return true;
            return false;
        }

        private static bool SameGateFamily(MethodBase left, MethodBase right)
        {
            if (!string.Equals(IOSHookIdentity.ForMethod(left), IOSHookIdentity.ForMethod(right), StringComparison.Ordinal))
                return false;
            Type leftType = left.DeclaringType;
            Type rightType = right.DeclaringType;
            if (leftType == null || rightType == null)
                return leftType == null && rightType == null;
            if (leftType.ContainsGenericParameters || rightType.ContainsGenericParameters)
                return leftType.ContainsGenericParameters && rightType.ContainsGenericParameters && leftType.Equals(rightType);
            return leftType.Equals(rightType);
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
            lock (Sync) registrations = GetRegistrationsForTargetLocked(target);

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

        private static Delegate DispatchTyped(MethodBase target, Type delegateType)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (delegateType == null) throw new ArgumentNullException(nameof(delegateType));

            lock (Sync)
            {
                Dictionary<Type, Delegate> byType;
                if (TypedDispatchCache.TryGetValue(target, out byType))
                {
                    Delegate cached;
                    if (byType.TryGetValue(delegateType, out cached)) return cached;
                }

                Type expectedType = GetTypedDelegateType(target);
                if (expectedType == null || expectedType != delegateType)
                    throw new InvalidOperationException("The typed iOS hook gate did not provide its generated signature delegate for " + target + ".");

                Registration[] registrations = GetRegistrationsForTargetLocked(target);
                Delegate result = CreateTypedPipeline(target, delegateType, registrations);
                if (byType == null)
                {
                    byType = new Dictionary<Type, Delegate>();
                    TypedDispatchCache[target] = byType;
                }
                byType[delegateType] = result;
                return result;
            }
        }

        private static Delegate CreateTypedPipeline(MethodBase target, Type delegateType, Registration[] registrations)
        {
            return CreateTypedPipelineContinuation(target, delegateType, registrations, 0);
        }

        private static Delegate CreateTypedPipelineContinuation(MethodBase target, Type delegateType,
            Registration[] registrations, int firstIndex)
        {
            MethodInfo delegateInvoke = delegateType.GetMethod("Invoke");
            if (delegateInvoke == null) throw new InvalidOperationException("The generated typed hook signature has no Invoke method.");

            MethodInfo modified = GetModifiedBody(target);
            Delegate original;
            if (modified != null)
            {
                MethodInfo originalMethod = modified;
                if (originalMethod.IsStatic)
                    original = Delegate.CreateDelegate(delegateType, originalMethod, true);
                else
                    original = Delegate.CreateDelegate(delegateType, null, originalMethod, true);
            }
            else
            {
                string adapterName = IOSHookIdentity.TypedOriginalAdapterName(target);
                MethodInfo originalAdapter = target.DeclaringType.GetMethod(adapterName,
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                if (originalAdapter == null)
                    throw new MissingMethodException("The iOS hook weave did not provide typed original adapter " + adapterName + " for " + target + ".");
                if (target is MethodInfo targetMethod && targetMethod.IsGenericMethod && !targetMethod.IsGenericMethodDefinition)
                    originalAdapter = originalAdapter.MakeGenericMethod(targetMethod.GetGenericArguments());
                original = Delegate.CreateDelegate(delegateType, originalAdapter, true);
            }

            if (registrations.Length == 0 || firstIndex >= registrations.Length) return original;

            Delegate[] chain = new Delegate[registrations.Length];
            for (int index = registrations.Length - 1; index >= firstIndex; index--)
            {
                Registration registration = registrations[index];
                MethodInfo hookMethod = CloseHookMethodForTarget(registration.HookMethod, target,
                    registration.IsDetour, registration.UseOriginalInstance);
                Delegate hook = registration.Hook;
                if (hook == null)
                    hook = CreateTypedMethodDelegate(registration, target);

                MethodInfo hookInvoke = hook.GetType().GetMethod("Invoke");
                if (hookInvoke == null)
                    throw new InvalidOperationException("Hook callback " + hook.GetType().FullName + " has no Invoke method.");
                ParameterInfo[] hookParameters = hookInvoke.GetParameters();
                ParameterInfo[] targetParameters = delegateInvoke.GetParameters();
                bool hasOrig = hookParameters.Length == targetParameters.Length + 1 &&
                    hookParameters.Length > 0 && typeof(Delegate).IsAssignableFrom(hookParameters[0].ParameterType);
                int hookOffset = hasOrig ? 1 : 0;
                if (hookParameters.Length != targetParameters.Length + hookOffset || hookInvoke.ReturnType != delegateInvoke.ReturnType)
                    throw new InvalidOperationException("Hook callback signature does not match typed target " + target + ".");
                for (int parameterIndex = 0; parameterIndex < targetParameters.Length; parameterIndex++)
                    if (hookParameters[parameterIndex + hookOffset].ParameterType != targetParameters[parameterIndex].ParameterType)
                        throw new InvalidOperationException("Hook callback parameter " + parameterIndex + " does not match typed target " + target + ".");

                Delegate orig = null;
                if (hasOrig)
                {
                    Type origType = hookParameters[0].ParameterType;
                    IOSTypedContinuationState state = new IOSTypedContinuationState
                    {
                        Chain = chain,
                        Original = original,
                        Index = index + 1
                    };
                    MethodInfo continuation = CreateTypedContinuationMethod(target, delegateType, delegateInvoke);
                    orig = Delegate.CreateDelegate(origType, state, continuation, true);
                }

                IOSTypedHookInvocationState hookState = new IOSTypedHookInvocationState
                {
                    Hook = hook,
                    Original = orig
                };
                MethodInfo adapter = CreateTypedHookAdapterMethod(target, delegateType, delegateInvoke,
                    hook.GetType(), hookInvoke, hasOrig);
                chain[index] = Delegate.CreateDelegate(delegateType, hookState, adapter, true);
            }

            return chain[firstIndex];
        }

        private static bool HaveSameTypedSignature(Type expectedType, Type actualType)
        {
            MethodInfo expected = expectedType.GetMethod("Invoke");
            MethodInfo actual = actualType.GetMethod("Invoke");
            if (expected == null || actual == null || expected.ReturnType != actual.ReturnType ||
                !SameCustomModifiers(expected.ReturnParameter, actual.ReturnParameter))
                return false;

            ParameterInfo[] expectedParameters = expected.GetParameters();
            ParameterInfo[] actualParameters = actual.GetParameters();
            if (expectedParameters.Length != actualParameters.Length) return false;
            for (int index = 0; index < expectedParameters.Length; index++)
                if (expectedParameters[index].ParameterType != actualParameters[index].ParameterType ||
                    !SameCustomModifiers(expectedParameters[index], actualParameters[index]))
                    return false;
            return true;
        }

        private static bool SameCustomModifiers(ParameterInfo left, ParameterInfo right)
        {
            return left.GetRequiredCustomModifiers().SequenceEqual(right.GetRequiredCustomModifiers()) &&
                left.GetOptionalCustomModifiers().SequenceEqual(right.GetOptionalCustomModifiers());
        }

        private static Delegate AdaptTypedDelegate(Delegate source, Type targetDelegateType)
        {
            if (source.GetType() == targetDelegateType) return source;
            return Delegate.CreateDelegate(targetDelegateType, source.Target, source.Method, true);
        }

        private static MethodInfo CreateTypedContinuationMethod(MethodBase target, Type delegateType, MethodInfo delegateInvoke)
        {
            ParameterInfo[] delegateParameters = delegateInvoke.GetParameters();
            string name = "TypedNext_" + IOSHookIdentity.ForMethod(target) + "_" + Interlocked.Increment(ref nextSequence);
            return CreateGeneratedMethod(name, delegateInvoke.ReturnParameter,
                new[] { typeof(IOSTypedContinuationState) }, delegateParameters, (module, method) =>
            {
                ILProcessor il = method.Body.GetILProcessor();
                il.Append(il.Create(OpCodes.Ldarg_0));
                MethodInfo getNext = typeof(IOSTypedHookBridge).GetMethod(nameof(IOSTypedHookBridge.GetNext), BindingFlags.Public | BindingFlags.Static);
                il.Append(il.Create(OpCodes.Call, module.ImportReference(getNext)));
                il.Append(il.Create(OpCodes.Castclass, module.ImportReference(delegateType)));
                for (int index = 0; index < delegateParameters.Length; index++)
                    il.Append(LoadCecilArgument(il, index + 1));
                il.Append(il.Create(OpCodes.Callvirt, CreateRuntimeInstanceMethodReference(module, delegateType, delegateInvoke)));
                il.Append(il.Create(OpCodes.Ret));
            }, AssemblyLoadContext.GetLoadContext(target.Module.Assembly) ?? AssemblyLoadContext.Default,
                delegateType.Assembly, typeof(IOSTypedHookBridge).Assembly);
        }

        private static MethodInfo CreateTypedHookAdapterMethod(MethodBase target, Type delegateType, MethodInfo delegateInvoke,
            Type hookDelegateType, MethodInfo hookInvoke, bool hasOrig)
        {
            ParameterInfo[] delegateParameters = delegateInvoke.GetParameters();
            string name = "TypedHook_" + IOSHookIdentity.ForMethod(target) + "_" + Interlocked.Increment(ref nextSequence);
            return CreateGeneratedMethod(name, delegateInvoke.ReturnParameter,
                new[] { typeof(IOSTypedHookInvocationState) }, delegateParameters, (module, method) =>
            {
                ILProcessor il = method.Body.GetILProcessor();
                FieldInfo hookField = typeof(IOSTypedHookInvocationState).GetField(nameof(IOSTypedHookInvocationState.Hook));
                FieldInfo origField = typeof(IOSTypedHookInvocationState).GetField(nameof(IOSTypedHookInvocationState.Original));
                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Ldfld, module.ImportReference(hookField)));
                il.Append(il.Create(OpCodes.Castclass, module.ImportReference(hookDelegateType)));
                if (hasOrig)
                {
                    il.Append(il.Create(OpCodes.Ldarg_0));
                    il.Append(il.Create(OpCodes.Ldfld, module.ImportReference(origField)));
                    Type origType = hookInvoke.GetParameters()[0].ParameterType;
                    il.Append(il.Create(OpCodes.Castclass, module.ImportReference(origType)));
                }
                for (int index = 0; index < delegateParameters.Length; index++)
                    il.Append(LoadCecilArgument(il, index + 1));
                il.Append(il.Create(OpCodes.Callvirt, CreateRuntimeInstanceMethodReference(module, hookDelegateType, hookInvoke)));
                il.Append(il.Create(OpCodes.Ret));
            }, AssemblyLoadContext.GetLoadContext(target.Module.Assembly) ?? AssemblyLoadContext.Default,
                delegateType.Assembly, hookDelegateType.Assembly, typeof(IOSTypedHookInvocationState).Assembly);
        }

        private static MethodInfo CreateGeneratedMethod(string name, ParameterInfo returnParameter,
            Type[] prefixParameterTypes, ParameterInfo[] parameters,
            Action<ModuleDefinition, Mono.Cecil.MethodDefinition> buildBody,
            AssemblyLoadContext loadContext, params Assembly[] preferredAssemblies)
        {
            AssemblyDefinition assembly = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition("CelesteIOS.Typed." + name, new Version(1, 0, 0, 0)),
                "CelesteIOS.Typed." + name,
                ModuleKind.Dll);
            ModuleDefinition module = assembly.MainModule;
            TypeDefinition helper = new TypeDefinition(
                "CelesteIOS.Generated",
                "TypedAdapter_" + Guid.NewGuid().ToString("N"),
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Abstract |
                Mono.Cecil.TypeAttributes.Sealed | Mono.Cecil.TypeAttributes.BeforeFieldInit,
                module.ImportReference(typeof(object)));
            module.Types.Add(helper);

            Mono.Cecil.MethodDefinition method = new Mono.Cecil.MethodDefinition(
                "Invoke",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static |
                Mono.Cecil.MethodAttributes.HideBySig,
                ImportRuntimeParameterSignature(module, returnParameter));
            foreach (Type parameterType in prefixParameterTypes)
                method.Parameters.Add(new ParameterDefinition(ImportRuntimeSignatureType(module, parameterType)));
            foreach (ParameterInfo parameter in parameters)
                method.Parameters.Add(new ParameterDefinition(parameter.Name,
                    (Mono.Cecil.ParameterAttributes)parameter.Attributes,
                    ImportRuntimeParameterSignature(module, parameter)));
            method.Body = new Mono.Cecil.Cil.MethodBody(method) { InitLocals = true, MaxStackSize = 16 };
            helper.Methods.Add(method);
            buildBody(module, method);

            IEnumerable<Assembly> signatureAssemblies = parameters
                .SelectMany(parameter => new[] { parameter.ParameterType.Assembly }
                    .Concat(parameter.GetRequiredCustomModifiers().Select(type => type.Assembly))
                    .Concat(parameter.GetOptionalCustomModifiers().Select(type => type.Assembly)))
                .Concat(new[] { returnParameter.ParameterType.Assembly })
                .Concat(returnParameter.GetRequiredCustomModifiers().Select(type => type.Assembly))
                .Concat(returnParameter.GetOptionalCustomModifiers().Select(type => type.Assembly));
            Assembly loaded = LoadGeneratedAssembly(assembly, loadContext,
                (preferredAssemblies ?? Array.Empty<Assembly>()).Concat(signatureAssemblies).Distinct().ToArray());
            Type helperType = loaded.GetType(helper.FullName, true, false);
            return helperType.GetMethod(method.Name, BindingFlags.Public | BindingFlags.Static);
        }

        private static TypeReference ImportRuntimeSignatureType(ModuleDefinition module, Type type)
        {
            if (type.IsByRef) return new ByReferenceType(ImportRuntimeSignatureType(module, type.GetElementType()));
            if (type.IsPointer) return new PointerType(ImportRuntimeSignatureType(module, type.GetElementType()));
            if (type.IsArray) return new ArrayType(ImportRuntimeSignatureType(module, type.GetElementType()), type.GetArrayRank());
            if (type.IsFunctionPointer)
            {
                FunctionPointerType functionPointer = new FunctionPointerType
                {
                    HasThis = false,
                    ExplicitThis = false,
                    CallingConvention = RuntimeFunctionPointerCallingConvention(type),
                    ReturnType = ImportRuntimeSignatureType(module, type.GetFunctionPointerReturnType())
                };
                foreach (Type parameterType in type.GetFunctionPointerParameterTypes())
                    functionPointer.Parameters.Add(new ParameterDefinition(ImportRuntimeSignatureType(module, parameterType)));
                return functionPointer;
            }
            return module.ImportReference(type);
        }

        private static MethodReference CreateRuntimeInstanceMethodReference(ModuleDefinition module, Type declaringType, MethodInfo method)
        {
            if (!ContainsRuntimeFunctionPointer(method.ReturnType) &&
                !method.GetParameters().Any(parameter => ContainsRuntimeFunctionPointer(parameter.ParameterType)))
                return module.ImportReference(method);

            MethodReference reference = new MethodReference(method.Name,
                ImportRuntimeParameterSignature(module, method.ReturnParameter),
                module.ImportReference(declaringType))
            {
                HasThis = !method.IsStatic,
                ExplicitThis = false,
                CallingConvention = MethodCallingConvention.Default
            };
            foreach (ParameterInfo parameter in method.GetParameters())
                reference.Parameters.Add(new ParameterDefinition(parameter.Name,
                    (Mono.Cecil.ParameterAttributes)parameter.Attributes,
                    ImportRuntimeParameterSignature(module, parameter)));
            return reference;
        }

        private static bool ContainsRuntimeFunctionPointer(Type type)
        {
            if (type.IsFunctionPointer) return true;
            if (type.HasElementType) return ContainsRuntimeFunctionPointer(type.GetElementType());
            return type.IsGenericType && type.GetGenericArguments().Any(ContainsRuntimeFunctionPointer);
        }

        private static MethodCallingConvention RuntimeFunctionPointerCallingConvention(Type type)
        {
            if (!type.IsUnmanagedFunctionPointer) return MethodCallingConvention.Default;
            foreach (Type convention in type.GetFunctionPointerCallingConventions())
            {
                if (convention.Name == "CallConvCdecl") return MethodCallingConvention.C;
                if (convention.Name == "CallConvStdcall") return MethodCallingConvention.StdCall;
                if (convention.Name == "CallConvThiscall") return MethodCallingConvention.ThisCall;
                if (convention.Name == "CallConvFastcall") return MethodCallingConvention.FastCall;
            }
            return MethodCallingConvention.Unmanaged;
        }

        private static Assembly LoadGeneratedAssembly(AssemblyDefinition assembly, AssemblyLoadContext loadContext,
            params Assembly[] preferredAssemblies)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                assembly.Write(stream);
                stream.Position = 0;
                TypedAssemblyResolver typedResolver = TypedResolvers.GetValue(loadContext, context =>
                {
                    TypedAssemblyResolver created = new TypedAssemblyResolver();
                    context.Resolving += created.Resolve;
                    return created;
                });
                if (preferredAssemblies != null)
                    foreach (Assembly preferred in preferredAssemblies)
                        typedResolver.Add(preferred);
                ResolveEventHandler appDomainResolver = (sender, args) => ResolveLoadedAssembly(args.Name, preferredAssemblies);
                AppDomain.CurrentDomain.AssemblyResolve += appDomainResolver;
                try
                {
                    return loadContext.LoadFromStream(stream);
                }
                finally
                {
                    AppDomain.CurrentDomain.AssemblyResolve -= appDomainResolver;
                }
            }
        }

        private static Delegate CreateTypedMethodDelegate(Registration registration, MethodBase target)
        {
            MethodInfo hookMethod = CloseHookMethodForTarget(registration.HookMethod, target,
                registration.IsDetour, registration.UseOriginalInstance);
            if (registration.UseOriginalInstance && !hookMethod.IsStatic)
                return CreateTypedInstanceDetour(registration, target, hookMethod);

            Type delegateType = CreateDelegateTypeForMethod(hookMethod);
            return hookMethod.IsStatic
                ? Delegate.CreateDelegate(delegateType, hookMethod, true)
                : Delegate.CreateDelegate(delegateType, registration.HookTarget, hookMethod, true);
        }

        private static Delegate CreateTypedInstanceDetour(Registration registration, MethodBase target, MethodInfo hookMethod)
        {
            Type delegateType = GetTypedDelegateType(target);
            MethodInfo delegateInvoke = delegateType.GetMethod("Invoke");
            ParameterInfo[] targetParameters = delegateInvoke.GetParameters();
            if (target.IsStatic || targetParameters.Length == 0)
                throw new NotSupportedException("A typed instance detour requires an instance target: " + target + ".");

            Type instanceParameter = targetParameters[0].ParameterType;
            if (instanceParameter.IsByRef)
            {
                Type instanceType = instanceParameter.GetElementType();
                if (instanceType == hookMethod.DeclaringType &&
                    (instanceType.IsValueType || instanceType.IsByRefLike))
                {
                    // A boxed struct would lose the caller's mutable this. An
                    // open instance delegate instead receives the original
                    // by-reference this pointer and invokes the replacement
                    // with the exact typed signature generated for the target.
                    return Delegate.CreateDelegate(delegateType, null, hookMethod, true);
                }
                throw new NotSupportedException("A typed value-type detour must be an instance method on the target value type: " + target + ".");
            }
            if (instanceParameter.IsValueType || instanceParameter.IsByRefLike)
                throw new NotSupportedException("Typed instance detours require an explicit by-reference target instance: " + target + ".");

            Type replacementDelegateType = CreateDelegateTypeForMethod(hookMethod);
            IOSTypedDetourInvocationState state = new IOSTypedDetourInvocationState
            {
                Method = hookMethod,
                DelegateType = replacementDelegateType
            };

            Type[] adapterPrefixParameters = new[] { typeof(IOSTypedDetourInvocationState) };
            string name = "TypedInstanceDetour_" + IOSHookIdentity.ForMethod(target) + "_" + Interlocked.Increment(ref nextSequence);
            MethodInfo adapter = CreateGeneratedMethod(name, delegateInvoke.ReturnParameter,
                adapterPrefixParameters, targetParameters, (module, method) =>
            {
                ILProcessor il = method.Body.GetILProcessor();
                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Ldarg_1));
                MethodInfo getBound = typeof(IOSTypedHookBridge).GetMethod(nameof(IOSTypedHookBridge.GetBoundDetour), BindingFlags.Public | BindingFlags.Static);
                il.Append(il.Create(OpCodes.Call, module.ImportReference(getBound)));
                il.Append(il.Create(OpCodes.Castclass, module.ImportReference(replacementDelegateType)));
                for (int index = 1; index < targetParameters.Length; index++)
                    il.Append(LoadCecilArgument(il, index + 1));
                il.Append(il.Create(OpCodes.Callvirt, CreateRuntimeInstanceMethodReference(module, replacementDelegateType,
                    replacementDelegateType.GetMethod("Invoke"))));
                il.Append(il.Create(OpCodes.Ret));
            }, AssemblyLoadContext.GetLoadContext(target.Module.Assembly) ?? AssemblyLoadContext.Default,
                delegateType.Assembly, replacementDelegateType.Assembly, typeof(IOSTypedHookBridge).Assembly);

            return Delegate.CreateDelegate(delegateType, state, adapter, true);
        }

        private static Type GetTypedDelegateType(MethodBase target)
        {
            string name = "CelesteIOS.Generated.__ios_typed_delegate_" + IOSHookIdentity.ForMethod(target);
            Type delegateType = target.Module.Assembly.GetType(name, false, false);
            if (delegateType == null)
                throw new InvalidOperationException("The typed iOS hook gate did not provide its generated signature delegate for " + target + ".");
            if (delegateType.IsGenericTypeDefinition)
            {
                Type[] declaringArguments = target.DeclaringType != null && target.DeclaringType.IsGenericType
                    ? target.DeclaringType.GetGenericArguments()
                    : Type.EmptyTypes;
                Type[] methodArguments = target is MethodInfo method && method.IsGenericMethod
                    ? method.GetGenericArguments()
                    : Type.EmptyTypes;
                Type[] arguments = declaringArguments.Concat(methodArguments).ToArray();
                if (arguments.Length != delegateType.GetGenericArguments().Length)
                    throw new InvalidOperationException("The typed iOS hook signature generic arity does not match " + target + ".");
                delegateType = delegateType.MakeGenericType(arguments);
            }
            return delegateType;
        }

        private static Type CreateDelegateTypeForMethod(MethodInfo method)
        {
            AssemblyLoadContext loadContext = AssemblyLoadContext.GetLoadContext(method.Module.Assembly) ?? AssemblyLoadContext.Default;
            string unique = Interlocked.Increment(ref nextSequence).ToString();
            string assemblyName = "CelesteIOS.TypedHookDelegate." + unique;
            AssemblyDefinition assembly = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition(assemblyName, new Version(1, 0, 0, 0)), assemblyName, ModuleKind.Dll);
            ModuleDefinition module = assembly.MainModule;
            TypeDefinition delegateType = new TypeDefinition(
                "CelesteIOS.Generated",
                "ReflectedHookDelegate_" + unique,
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Sealed |
                Mono.Cecil.TypeAttributes.Class | Mono.Cecil.TypeAttributes.AnsiClass |
                Mono.Cecil.TypeAttributes.AutoClass,
                module.ImportReference(typeof(MulticastDelegate)));
            module.Types.Add(delegateType);

            Mono.Cecil.MethodDefinition constructor = new Mono.Cecil.MethodDefinition(
                ".ctor",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig |
                Mono.Cecil.MethodAttributes.SpecialName | Mono.Cecil.MethodAttributes.RTSpecialName,
                module.TypeSystem.Void)
            {
                ImplAttributes = Mono.Cecil.MethodImplAttributes.Runtime | Mono.Cecil.MethodImplAttributes.Managed,
                HasThis = true
            };
            constructor.Parameters.Add(new ParameterDefinition("target", Mono.Cecil.ParameterAttributes.None, module.TypeSystem.Object));
            constructor.Parameters.Add(new ParameterDefinition("method", Mono.Cecil.ParameterAttributes.None, module.TypeSystem.IntPtr));
            delegateType.Methods.Add(constructor);

            Mono.Cecil.MethodDefinition invoke = new Mono.Cecil.MethodDefinition(
                "Invoke",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig |
                Mono.Cecil.MethodAttributes.NewSlot | Mono.Cecil.MethodAttributes.Virtual,
                ImportRuntimeParameterSignature(module, method.ReturnParameter))
            {
                ImplAttributes = Mono.Cecil.MethodImplAttributes.Runtime | Mono.Cecil.MethodImplAttributes.Managed,
                HasThis = true
            };
            foreach (ParameterInfo parameter in method.GetParameters())
                invoke.Parameters.Add(new ParameterDefinition(parameter.Name, (Mono.Cecil.ParameterAttributes)parameter.Attributes,
                    ImportRuntimeParameterSignature(module, parameter)));
            delegateType.Methods.Add(invoke);

            Assembly[] dependencies = method.GetParameters().SelectMany(parameter =>
                    new[] { parameter.ParameterType.Assembly }.Concat(parameter.GetRequiredCustomModifiers().Select(type => type.Assembly))
                        .Concat(parameter.GetOptionalCustomModifiers().Select(type => type.Assembly)))
                .Concat(new[] { method.ReturnType.Assembly, method.Module.Assembly })
                .Concat(method.ReturnParameter.GetRequiredCustomModifiers().Select(type => type.Assembly))
                .Concat(method.ReturnParameter.GetOptionalCustomModifiers().Select(type => type.Assembly))
                .Distinct().ToArray();
            Assembly loaded = LoadGeneratedAssembly(assembly, loadContext, dependencies);
            return loaded.GetType(delegateType.FullName, true, false);
        }

        private static TypeReference ImportRuntimeParameterSignature(ModuleDefinition module, ParameterInfo parameter)
        {
            TypeReference type = ImportRuntimeSignatureType(module, parameter.ParameterType);
            foreach (Type modifier in parameter.GetRequiredCustomModifiers())
                type = new RequiredModifierType(module.ImportReference(modifier), type);
            foreach (Type modifier in parameter.GetOptionalCustomModifiers())
                type = new OptionalModifierType(module.ImportReference(modifier), type);
            return type;
        }

        private static Assembly ResolveLoadedAssembly(string requestedName, Assembly[] preferredAssemblies)
        {
            AssemblyName requested;
            try { requested = new AssemblyName(requestedName); }
            catch { return null; }

            if (preferredAssemblies != null)
                foreach (Assembly preferred in preferredAssemblies)
                    if (preferred != null && AssemblyName.ReferenceMatchesDefinition(requested, preferred.GetName()))
                        return preferred;

            foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
                if (AssemblyName.ReferenceMatchesDefinition(requested, loaded.GetName()))
                    return loaded;
            return null;
        }

        private static Instruction LoadCecilArgument(ILProcessor il, int index)
        {
            switch (index)
            {
                case 0: return il.Create(OpCodes.Ldarg_0);
                case 1: return il.Create(OpCodes.Ldarg_1);
                case 2: return il.Create(OpCodes.Ldarg_2);
                case 3: return il.Create(OpCodes.Ldarg_3);
                default: return il.Create(OpCodes.Ldarg, (short)index);
            }
        }

        private static MethodInfo FindOriginalMethod(MethodBase target)
        {
            MethodInfo method = target as MethodInfo;
            bool constructor = target is ConstructorInfo;
            if (method == null && !constructor)
                throw new NotSupportedException("The iOS gate cannot resolve an original body for " + target + ".");

            string name = IOSHookIdentity.OriginalMethodName(target);
            if (IsModuleInitializer(target))
            {
                Type container = target.Module.Assembly.GetType(IOSHookIdentity.ModuleOriginalContainerName(target), false, false);
                MethodInfo moduleOriginal = container?.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (moduleOriginal == null)
                    throw new MissingMethodException("The iOS hook weave did not provide the module-level original body for " + target + ".");
                return moduleOriginal;
            }
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

        private static bool IsModuleInitializer(MethodBase target)
        {
            Type declaringType = target?.DeclaringType;
            // CoreCLR exposes a global <Module> method with a null declaring
            // type, while the iOS interpreter exposes the synthetic <Module>
            // Type. Both refer to the same module initializer and its generated
            // original body/gate live in the weaver's per-method container.
            return declaringType == null || declaringType.Name == "<Module>";
        }

        private static MethodInfo CloseHookMethodForTarget(MethodInfo hookMethod, MethodBase target,
            bool isDetour, bool bindsOriginalInstance)
        {
            if (hookMethod == null) throw new ArgumentNullException(nameof(hookMethod));
            if (target == null) throw new ArgumentNullException(nameof(target));

            Exception lastFailure = null;
            foreach (MethodInfo declaringTypeMethod in GetHookDeclaringTypeVariants(hookMethod, target))
            {
                Type[][] candidates = GetTargetGenericArgumentCandidates(target);
                if (!declaringTypeMethod.IsGenericMethodDefinition)
                {
                    try
                    {
                        ValidateHookOrDetour(target, declaringTypeMethod, isDetour, bindsOriginalInstance);
                        return declaringTypeMethod;
                    }
                    catch (Exception exception) when (IsSignatureBindingFailure(exception))
                    {
                        lastFailure = exception;
                    }
                    continue;
                }

                foreach (Type[] arguments in candidates)
                {
                    if (arguments.Length != declaringTypeMethod.GetGenericArguments().Length) continue;
                    try
                    {
                        MethodInfo closed = declaringTypeMethod.MakeGenericMethod(arguments);
                        ValidateHookOrDetour(target, closed, isDetour, bindsOriginalInstance);
                        return closed;
                    }
                    catch (Exception exception) when (IsSignatureBindingFailure(exception))
                    {
                        lastFailure = exception;
                    }
                }
            }

            throw new NotSupportedException(
                "The reflected hook or detour signature cannot be mapped to the generic target " + target + ".",
                lastFailure);
        }

        private static IEnumerable<MethodInfo> GetHookDeclaringTypeVariants(MethodInfo hookMethod, MethodBase target)
        {
            Type declaringType = hookMethod.DeclaringType;
            if (declaringType == null || !declaringType.ContainsGenericParameters)
            {
                yield return hookMethod;
                yield break;
            }
            if (!declaringType.IsGenericType)
                throw new NotSupportedException("The hook method's generic declaring type cannot be closed: " + hookMethod + ".");

            Type definition = declaringType.GetGenericTypeDefinition();
            Type[] targetDeclaringArguments = target.DeclaringType != null && target.DeclaringType.IsGenericType
                ? target.DeclaringType.GetGenericArguments()
                : Type.EmptyTypes;
            Type[][] candidates = new[] { targetDeclaringArguments }
                .Concat(GetTargetGenericArgumentCandidates(target))
                .ToArray();
            foreach (Type[] arguments in candidates)
            {
                if (arguments.Length != definition.GetGenericArguments().Length) continue;
                Type closedType;
                try { closedType = definition.MakeGenericType(arguments); }
                catch (ArgumentException) { continue; }

                MethodInfo closedMethod = closedType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .FirstOrDefault(candidate => candidate.Module == hookMethod.Module &&
                        candidate.MetadataToken == hookMethod.MetadataToken);
                if (closedMethod == null) continue;

                if (hookMethod.IsGenericMethod && !hookMethod.IsGenericMethodDefinition &&
                    closedMethod.IsGenericMethodDefinition)
                {
                    try { closedMethod = closedMethod.MakeGenericMethod(hookMethod.GetGenericArguments()); }
                    catch (ArgumentException) { continue; }
                }
                yield return closedMethod;
            }
        }

        private static Type[][] GetTargetGenericArgumentCandidates(MethodBase target)
        {
            Type[] declaringArguments = target.DeclaringType != null && target.DeclaringType.IsGenericType
                ? target.DeclaringType.GetGenericArguments()
                : Type.EmptyTypes;
            Type[] methodArguments = target is MethodInfo method && method.IsGenericMethod
                ? method.GetGenericArguments()
                : Type.EmptyTypes;
            List<Type[]> candidates = new List<Type[]>();
            AddCandidate(declaringArguments.Concat(methodArguments).ToArray());
            AddCandidate(methodArguments);
            AddCandidate(declaringArguments);
            AddCandidate(Type.EmptyTypes);
            return candidates.ToArray();

            void AddCandidate(Type[] candidate)
            {
                if (!candidates.Any(existing => existing.SequenceEqual(candidate)))
                    candidates.Add(candidate);
            }
        }

        private static void ValidateHookOrDetour(MethodBase target, MethodInfo method,
            bool isDetour, bool bindsOriginalInstance)
        {
            if (isDetour)
                ValidateDetourSignature(target, method, bindsOriginalInstance);
            else
                ValidateHookSignature(target, method);
        }

        private static bool IsSignatureBindingFailure(Exception exception)
        {
            return exception is ArgumentException || exception is InvalidOperationException ||
                exception is NotSupportedException || exception is TypeLoadException;
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
                ParameterInfo[] originalParameters = invoke.GetParameters();
                if (originalParameters.Length != targetArgumentCount)
                    throw new InvalidOperationException("The orig delegate parameter count does not match " + target + ".");
                int originalOffset = 0;
                if (!target.IsStatic)
                {
                    Type instanceType = target.DeclaringType.IsValueType
                        ? target.DeclaringType.MakeByRefType()
                        : target.DeclaringType;
                    if (originalParameters[0].ParameterType != instanceType)
                        throw new InvalidOperationException("The orig delegate instance parameter does not match " + target + ".");
                    originalOffset = 1;
                }
                for (int i = 0; i < targetParameters.Length; i++)
                    if (originalParameters[i + originalOffset].ParameterType != targetParameters[i].ParameterType)
                        throw new InvalidOperationException("The orig delegate parameter " + i + " does not match " + target + ".");
            }

            Type targetReturn = (target as MethodInfo)?.ReturnType ?? typeof(void);
            if (hookMethod.ReturnType != targetReturn)
                throw new InvalidOperationException("The hook return type does not match " + target + ".");
        }

        private static void ValidateDetourSignature(MethodBase target, MethodInfo replacement, bool bindsOriginalInstance)
        {
            if (!(target is MethodInfo) && !(target is ConstructorInfo))
                throw new NotSupportedException("The iOS detour target is not a managed method or constructor: " + target);
            if (target.IsStatic && !replacement.IsStatic)
                throw new InvalidOperationException("A static method cannot be used as the instance target of a detour on " + target + ".");
            if (bindsOriginalInstance && !replacement.IsStatic &&
                !replacement.DeclaringType.IsAssignableFrom(target.DeclaringType))
                throw new InvalidOperationException("The instance detour method is not declared on a type compatible with " + target + ".");
            ParameterInfo[] targetParameters = target.GetParameters();
            ParameterInfo[] replacementParameters = replacement.GetParameters();
            if (replacementParameters.Length != targetParameters.Length +
                (target.IsStatic || bindsOriginalInstance ? 0 : 1))
                throw new InvalidOperationException("The detour signature does not match " + target + ".");
            Type targetReturn = target is MethodInfo targetMethod ? targetMethod.ReturnType : typeof(void);
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
            string typedDelegateName = "CelesteIOS.Generated.__ios_typed_delegate_" + IOSHookIdentity.ForMethod(target);
            if (target.Module.Assembly.GetType(typedDelegateName, false, false) != null)
            {
                Type expectedDelegate = GetTypedDelegateType(target);
                if (!HaveSameTypedSignature(expectedDelegate, delegateType))
                    throw new InvalidOperationException("The typed original delegate does not match " + target + ".");
                Delegate originalTyped = CreateTypedPipelineContinuation(target, expectedDelegate, Array.Empty<Registration>(), 0);
                return AdaptTypedDelegate(originalTyped, delegateType);
            }
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
            List<string> additionalResolverDirectories = new List<string> { AppContext.BaseDirectory };
            if (!string.IsNullOrWhiteSpace(gameAssemblyPath))
                additionalResolverDirectories.Add(Path.GetDirectoryName(gameAssemblyPath));
            foreach (Assembly loadedAssembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string location = null;
                try { location = loadedAssembly.Location; } catch { }
                if (!string.IsNullOrWhiteSpace(location) && File.Exists(location))
                    additionalResolverDirectories.Add(Path.GetDirectoryName(location));
            }
            byte[] result = global::IOSHookWeaver.WeaveAssembly(bytes, out assemblyName, out woven, out skipped,
                resolverDirectory, additionalResolverDirectories, IOSILHookRuntime.GetAssemblySnapshotImages());
            if (woven > 0)
                Console.WriteLine("iOS hook backend wove " + woven + " methods in mod assembly " + assemblyName + ".");
            if (skipped.Length > 0)
            {
                Console.WriteLine("iOS hook backend left " + skipped.Length + " unsupported method bodies unhookable in " + assemblyName + ".");
                foreach (string method in skipped)
                    Console.WriteLine("iOS hook backend skipped: " + method);
            }
            return result;
        }
    }
}
