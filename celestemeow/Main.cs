using System;
using System.Reflection;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Foundation;
using UIKit;

namespace celestemeow
{
    public class Application
    {
        static StreamWriter startupWriter;
        static UIWindow modManagerWindow;
        static ModManagerViewController modManagerController;
        static bool celesteIsRunning;
        static string appBundleDirectory;
        static bool celesteRestartRequested;
        static readonly object DiagnosticsLock = new object();
        static string startupErrorPath;

        static void RegisterIOSClipboardDelegates()
        {
            AppDomain.CurrentDomain.SetData("Celeste.IOSClipboard.GetText", (Func<string>)GetClipboardText);
            AppDomain.CurrentDomain.SetData("Celeste.IOSClipboard.SetText", (Action<string>)SetClipboardText);
            AppDomain.CurrentDomain.SetData("Celeste.IOSClipboard.GetTextAsync",
                (Func<CancellationToken, Task<string>>)GetClipboardTextAsync);
            AppDomain.CurrentDomain.SetData("Celeste.IOSClipboard.SetTextAsync",
                (Func<string, CancellationToken, Task>)SetClipboardTextAsync);
            AppDomain.CurrentDomain.SetData("Celeste.IOSExternalLinks.OpenUrl", (Action<string>)OpenExternalUrl);
        }

        static void OpenExternalUrl(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return;

            UIApplication.SharedApplication.InvokeOnMainThread(() =>
                UIApplication.SharedApplication.OpenUrl(new NSUrl(uri.AbsoluteUri)));
        }

        static string GetClipboardText()
        {
            string value = string.Empty;
            UIApplication.SharedApplication.InvokeOnMainThread(() => value = UIPasteboard.General.String ?? string.Empty);
            return value;
        }

        static void SetClipboardText(string value)
        {
            UIApplication.SharedApplication.InvokeOnMainThread(() => UIPasteboard.General.String = value ?? string.Empty);
        }

        static Task<string> GetClipboardTextAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<string>(cancellationToken);

            TaskCompletionSource<string> completion = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenRegistration registration = cancellationToken.Register(
                () => completion.TrySetCanceled(cancellationToken));
            UIApplication.SharedApplication.BeginInvokeOnMainThread(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    registration.Dispose();
                    return;
                }
                try { completion.TrySetResult(UIPasteboard.General.String ?? string.Empty); }
                catch (Exception exception) { completion.TrySetException(exception); }
                finally { registration.Dispose(); }
            });
            return completion.Task;
        }

        static Task SetClipboardTextAsync(string value, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled(cancellationToken);

            TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenRegistration registration = cancellationToken.Register(
                () => completion.TrySetCanceled(cancellationToken));
            UIApplication.SharedApplication.BeginInvokeOnMainThread(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    registration.Dispose();
                    return;
                }
                try
                {
                    UIPasteboard.General.String = value ?? string.Empty;
                    completion.TrySetResult(true);
                }
                catch (Exception exception) { completion.TrySetException(exception); }
                finally { registration.Dispose(); }
            });
            return completion.Task;
        }

        [DllImport("celeste-ios-native", EntryPoint = "SDL_iPhoneSetEventPump", CallingConvention = CallingConvention.Cdecl)]
        static extern void SetSdlEventPump(int enabled);

#pragma warning disable CA2255
        [ModuleInitializer]
        internal static void InitializeStartupDiagnostics()
        {
            try
            {
                string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                Directory.CreateDirectory(documents);
                string path = Path.Combine(documents, "celeste-startup-trace.txt");
                startupErrorPath = Path.Combine(documents, "celeste-startup-error.txt");
                startupWriter = new StreamWriter(
                    new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    AutoFlush = true
                };
                Console.SetOut(startupWriter);
                Console.SetError(startupWriter);

                appBundleDirectory = NSBundle.MainBundle.BundlePath;
                if (string.IsNullOrWhiteSpace(appBundleDirectory))
                    appBundleDirectory = AppContext.BaseDirectory;

                IOSRuntimeAssemblyResolver.Attach(
                    AssemblyLoadContext.Default,
                    appBundleDirectory,
                    TraceStartup);
                TraceStartup("managed runtime assembly resolver registered; bundle=" + appBundleDirectory + "; base=" + AppContext.BaseDirectory);

                AppDomain.CurrentDomain.UnhandledException += HandleUnhandledException;
                TaskScheduler.UnobservedTaskException += HandleUnobservedTaskException;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("Could not initialize startup trace: " + exception);
            }

            TraceStartup("managed module initializer entered");
        }
#pragma warning restore CA2255

        static void HandleUnhandledException(object sender, UnhandledExceptionEventArgs eventArgs)
        {
            Exception exception = eventArgs.ExceptionObject as Exception;
            string details = exception == null ? Convert.ToString(eventArgs.ExceptionObject) : exception.ToString();
            WriteExceptionReport("Unhandled managed exception; terminating=" + eventArgs.IsTerminating, details);
        }

        static void HandleUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs eventArgs)
        {
            WriteExceptionReport("Unobserved task exception", eventArgs.Exception == null ? "Unknown task exception." : eventArgs.Exception.ToString());
            eventArgs.SetObserved();
        }

        static void WriteExceptionReport(string source, string details)
        {
            string report = "[" + DateTime.UtcNow.ToString("O") + "] " + source + Environment.NewLine +
                (details ?? "No exception details were available.") + Environment.NewLine;
            lock (DiagnosticsLock)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(startupErrorPath))
                        File.AppendAllText(startupErrorPath, report + Environment.NewLine);
                }
                catch { }

                try
                {
                    startupWriter?.WriteLine(report);
                    startupWriter?.Flush();
                }
                catch { }
            }
        }

        static void TraceStartup(string message)
        {
            try
            {
                Console.WriteLine(DateTime.UtcNow.ToString("O") + " " + message);
            }
            catch
            {
            }
        }

        static void Main(string[] args)
#if __IOS__ || __TVOS__
        {
            try
            {
                /*
                Console.WriteLine("adding silly exception listener");
                AppDomain.CurrentDomain.FirstChanceException += (sender, eventArgs) => {
                    Console.WriteLine("EXCEPTION: " + eventArgs.Exception.ToString());
                };
                */

                Console.WriteLine("running from ios - enabling workarounds!");
                // Enable high DPI "Retina" support. Trust us, you'll want this.
                Environment.SetEnvironmentVariable("FNA_GRAPHICS_ENABLE_HIGHDPI", "1");

                // Keep mouse and touch input separate.
                TraceStartup("setting SDL mouse/touch hints");
                IOSSDLInterop.SetHint("SDL_MOUSE_TOUCH_EVENTS", "0");
                IOSSDLInterop.SetHint("SDL_TOUCH_MOUSE_EVENTS", "0");

                Console.WriteLine("setting SDL hint FNA3D_FORCE_DRIVER to Vulkan");
                IOSSDLInterop.SetHint("FNA3D_FORCE_DRIVER", "Vulkan");
                TraceStartup("SDL hints set");

                realArgs = args;
                TraceStartup("calling SDL_UIKitRunApp");
                IOSSDLInterop.UIKitRunApp(0, IntPtr.Zero, FakeMain);
                TraceStartup("SDL_UIKitRunApp returned");
            }
            catch (Exception exception)
            {
                ReportStartupException(exception);
                ShowStartupException(exception);
            }
        }

        static string[] realArgs;

        [ObjCRuntime.MonoPInvokeCallback(typeof(IOSSDLInterop.SDLMainFunction))]
        static int FakeMain(int argc, IntPtr argv)
        {
            TraceStartup("SDL main callback entered; argc=" + argc);
            try
            {
                TraceStartup("showing pre-boot mod manager");
                modManagerController = new ModManagerViewController(StartCeleste);
                modManagerWindow = new UIWindow(UIScreen.MainScreen.Bounds)
                {
                    RootViewController = modManagerController,
                    WindowLevel = UIWindowLevel.Normal + 2
                };
                modManagerWindow.MakeKeyAndVisible();
                return 0;
            }
            catch (Exception exception)
            {
                ReportStartupException(exception);
                ShowStartupException(exception);
                return -1;
            }
        }

        static void StartCeleste()
        {
            if (celesteIsRunning)
                return;

            celesteIsRunning = true;
            celesteRestartRequested = false;
            Exception launchException = null;
            try
            {
                TraceStartup("enabling SDL event pump and starting game");
                modManagerWindow.Hidden = true;
                SetSdlEventPump(1);
                RealMain(realArgs);
                TraceStartup("Celeste returned to mod manager");
            }
            catch (Exception exception)
            {
                ReportStartupException(exception);
                launchException = exception;
            }
            finally
            {
                try { SetSdlEventPump(0); } catch { }
                modManagerWindow.Hidden = false;
                modManagerWindow.MakeKeyAndVisible();
                bool restartRequested = celesteRestartRequested;
                celesteRestartRequested = false;
                modManagerController?.GameDidReturn(restartRequested);
                if (launchException != null)
                    modManagerController?.ShowProblem(launchException);
                celesteIsRunning = false;
            }
        }

        static void RequestCelesteRestart()
        {
            celesteRestartRequested = true;
            TraceStartup("Everest requested a full restart; returning to the launcher for a clean app relaunch.");
        }

        static void ReportStartupException(Exception exception)
        {
            TraceStartup("startup exception captured: " + exception.GetType().FullName);
            WriteExceptionReport("Celeste iOS startup or launch failure", exception == null ? "Unknown exception." : exception.ToString());
        }

        internal static string[] GetDiagnosticFilePaths()
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string[] names = { "celeste-startup-trace.txt", "celeste-startup-error.txt", "log.txt", "errorLog.txt" };
            var paths = names.Select(name => Path.Combine(documents, name))
                .Concat(new[] { Path.Combine(IOSModLibrary.DataPath, "log.txt") });
            string crashLogDirectory = Path.Combine(IOSModLibrary.DataPath, "CrashLogs");
            if (Directory.Exists(crashLogDirectory))
                paths = paths.Concat(Directory.GetFiles(crashLogDirectory, "*.txt"));
            return paths
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        static void ShowStartupException(Exception exception)
        {
            try
            {
                string message = exception.ToString();
                if (message.Length > 2500)
                    message = message.Substring(0, 2500) + "\n\nFull details were saved as celeste-startup-error.txt.";

                IOSSDLInterop.ShowMessageBox(
                    IOSSDLInterop.MessageBoxError,
                    "Celeste could not start",
                    message,
                    IntPtr.Zero);
            }
            catch (Exception dialogException)
            {
                Console.Error.WriteLine("Could not show startup details: " + dialogException);
            }
        }

        static void RealMain(string[] args)
#endif
        {
            TraceStartup("RealMain entered");
            IOSNativeImports.EnsureRequiredImportsAreLinked();
            RegisterIOSClipboardDelegates();
            IOSHookRuntime.Initialize();
            string dataDirectory = IOSModLibrary.DataPath;
            string temporaryDirectory = Path.Combine(dataDirectory, "tmp");
            Directory.CreateDirectory(temporaryDirectory);
            Environment.SetEnvironmentVariable("EVEREST_TMPDIR", temporaryDirectory);
            AppDomain.CurrentDomain.SetData("Celeste.IOSHookRuntime.EverestDataPath", dataDirectory);
            AppDomain.CurrentDomain.SetData("Celeste.IOSHookRuntime.RequestCelesteRestart", (Action)RequestCelesteRestart);

            string bundleDirectory = string.IsNullOrWhiteSpace(appBundleDirectory)
                ? NSBundle.MainBundle.BundlePath
                : appBundleDirectory;
            if (string.IsNullOrWhiteSpace(bundleDirectory))
                bundleDirectory = AppContext.BaseDirectory;

            string gameAssemblyPath = IOSRuntimeAssemblyResolver.FindAssemblyPath(bundleDirectory, "Celeste.dll");
            if (string.IsNullOrWhiteSpace(gameAssemblyPath) &&
                !string.Equals(bundleDirectory, AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase))
                gameAssemblyPath = IOSRuntimeAssemblyResolver.FindAssemblyPath(AppContext.BaseDirectory, "Celeste.dll");
            if (string.IsNullOrWhiteSpace(gameAssemblyPath))
                throw new FileNotFoundException(
                    "Celeste.dll is missing from the app bundle. Searched " + bundleDirectory + " and " + AppContext.BaseDirectory + ".",
                    "Celeste.dll");

            TraceStartup("loading Celeste assembly from " + gameAssemblyPath);
            IOSHookRuntime.ConfigureGameAssembly(gameAssemblyPath);

            Assembly gameAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(gameAssemblyPath);
            Type celesteType = gameAssembly.GetType("Celeste.Celeste", throwOnError: true);
            MethodInfo method = celesteType.GetMethod(
                "Main",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(string[]) },
                modifiers: null);
            if (method == null || method.ReturnType != typeof(void) || !method.IsStatic)
            {
                TraceStartup("Celeste.Main(string[]) reflection lookup failed");
                throw new MissingMethodException("Could not find static void Celeste.Celeste.Main(string[]).");
            }

            TraceStartup("invoking patched Celeste.Main");
            method.Invoke(null, new object[] { args });
            TraceStartup("patched Celeste.Main returned");
        }
    }
}
