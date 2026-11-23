using System;
using System.Reflection;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Foundation;
using SDL2;
using UIKit;

namespace celestemeow
{
    public class Application
    {
        static StreamWriter startupWriter;
        static UIWindow modManagerWindow;
        static ModManagerViewController modManagerController;
        static bool celesteIsRunning;
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

        [DllImport("__Internal", EntryPoint = "SDL_iPhoneSetEventPump", CallingConvention = CallingConvention.Cdecl)]
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
                SDL2.SDL.SDL_SetHint(SDL.SDL_HINT_MOUSE_TOUCH_EVENTS, "0");
                SDL2.SDL.SDL_SetHint(SDL.SDL_HINT_TOUCH_MOUSE_EVENTS, "0");

                Console.WriteLine("setting SDL hint FNA3D_FORCE_DRIVER to Vulkan");
                SDL2.SDL.SDL_SetHint("FNA3D_FORCE_DRIVER", "Vulkan");
                TraceStartup("SDL hints set");

                realArgs = args;
                TraceStartup("calling SDL_UIKitRunApp");
                SDL2.SDL.SDL_UIKitRunApp(0, IntPtr.Zero, FakeMain);
                TraceStartup("SDL_UIKitRunApp returned");
            }
            catch (Exception exception)
            {
                ReportStartupException(exception);
                ShowStartupException(exception);
            }
        }

        static string[] realArgs;

        [ObjCRuntime.MonoPInvokeCallback(typeof(SDL2.SDL.SDL_main_func))]
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
                modManagerController?.GameDidReturn();
                if (launchException != null)
                    modManagerController?.ShowProblem(launchException);
                celesteIsRunning = false;
            }
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
            return names.Select(name => Path.Combine(documents, name))
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

                SDL2.SDL.SDL_ShowSimpleMessageBox(
                    SDL2.SDL.SDL_MessageBoxFlags.SDL_MESSAGEBOX_ERROR,
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
            string gameAssemblyPath = typeof(Celeste.Celeste).Assembly.Location;
            if (string.IsNullOrEmpty(gameAssemblyPath))
                gameAssemblyPath = Path.Combine(AppContext.BaseDirectory, "Celeste.dll");
            IOSHookRuntime.ConfigureGameAssembly(gameAssemblyPath);
            AppDomain.CurrentDomain.SetData("Celeste.IOSHookRuntime.EverestDataPath", IOSModLibrary.DataPath);

            // thanks to pixel#0772 on the celeste discord server for this <3
            //Type MeWhenIStealCodeFromAScreenshotInTheCelesteDiscordServer = typeof(UIWindow).GetType();
            MethodInfo method = typeof(Celeste.Celeste).GetMethod("Main", BindingFlags.NonPublic | BindingFlags.Static);
            if (method == null)
            {
                TraceStartup("Celeste.Main reflection lookup failed");
                throw new MissingMethodException("Could not find Celeste.Celeste.Main");
            }

            TraceStartup("invoking Celeste.Main");
            method.Invoke(null, new object[] { args });
            TraceStartup("Celeste.Main returned");
        }
    }
}
