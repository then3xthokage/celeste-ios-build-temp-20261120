using System.Reflection;
using System.Runtime.Loader;

if (args.Length != 2)
    throw new ArgumentException("Expected the woven runtime directory and the hook runtime assembly path.");

string runtimeDirectory = Path.GetFullPath(args[0]);
string hookRuntimePath = Path.GetFullPath(args[1]);
foreach (string path in Directory.GetFiles(runtimeDirectory, "*.dll").OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
    AssemblyLoadContext.Default.LoadFromAssemblyPath(path);

Assembly hookRuntimeAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(hookRuntimePath);
Type hookRuntime = hookRuntimeAssembly.GetType("celestemeow.IOSHookRuntime", true)!;
hookRuntime.GetMethod("Initialize", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);

Assembly wovenUtils = AssemblyLoadContext.Default.Assemblies.Single(assembly => assembly.GetName().Name == "MonoMod.Utils");
Type switches = wovenUtils.GetType("MonoMod.Switches", true)!;
switches.GetMethod("SetSwitchValue", BindingFlags.Public | BindingFlags.Static)!
    .Invoke(null, new object[] { "DMDType", "cecil" });
Type dmdType = wovenUtils.GetType("MonoMod.Utils.DynamicMethodDefinition", true)!;
object dmd = Activator.CreateInstance(dmdType, "ios_dmd_smoke", typeof(int), Type.EmptyTypes)!;
object generator = dmdType.GetMethod("GetILGenerator")!.Invoke(dmd, null)!;
generator.GetType().GetMethod("Emit", new[] { typeof(System.Reflection.Emit.OpCode), typeof(int) })!
    .Invoke(generator, new object[] { System.Reflection.Emit.OpCodes.Ldc_I4, 42 });
generator.GetType().GetMethod("Emit", new[] { typeof(System.Reflection.Emit.OpCode) })!
    .Invoke(generator, new object[] { System.Reflection.Emit.OpCodes.Ret });
MethodInfo generate = dmdType.GetMethods().Single(method => method.Name == "Generate" &&
    !method.IsGenericMethod && method.GetParameters().Length == 0);
MethodInfo generated = (MethodInfo)generate.Invoke(dmd, null)!;
object? answer = generated.Invoke(null, null);
if (answer is not int value || value != 42)
    throw new InvalidOperationException("Woven MonoMod's Cecil DynamicMethodDefinition backend returned an unexpected value.");

Console.WriteLine("Woven MonoMod/Cecil probe passed: startup, module initializers, hook dispatcher, DMD generation, and execution.");
