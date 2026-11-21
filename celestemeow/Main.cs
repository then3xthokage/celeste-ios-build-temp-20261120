using System;
using System.Reflection;
using System.IO;
using SDL2;

namespace celestemeow
{
    public class Application
    {
        static StreamWriter startupWriter;

        static void InitializeStartupDiagnostics()
        {
            try
            {
                string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string path = Path.Combine(documents, "celeste-startup-trace.txt");
                startupWriter = new StreamWriter(
                    new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                {
                    AutoFlush = true
                };
                Console.SetOut(startupWriter);
                Console.SetError(startupWriter);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("Could not initialize startup trace: " + exception);
            }

            TraceStartup("managed Main entered");
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
            InitializeStartupDiagnostics();
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

                Console.WriteLine("setting SDL hint FNA3D_FORCE_DRIVER to Metal");
                SDL2.SDL.SDL_SetHint("FNA3D_FORCE_DRIVER", "Metal");
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
                TraceStartup("calling RealMain");
                RealMain(realArgs);
                TraceStartup("RealMain returned");
                return 0;
            }
            catch (Exception exception)
            {
                ReportStartupException(exception);
                ShowStartupException(exception);
                return -1;
            }
        }

        static void ReportStartupException(Exception exception)
        {
            string report = "Celeste iOS startup failed at " + DateTime.UtcNow.ToString("O") + Environment.NewLine + exception;
            TraceStartup("startup exception captured: " + exception.GetType().FullName);
            Console.Error.WriteLine(report);

            try
            {
                string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string logPath = Path.Combine(documents, "celeste-startup-error.txt");
                File.WriteAllText(logPath, report);
            }
            catch (Exception logException)
            {
                Console.Error.WriteLine("Could not save startup details: " + logException);
            }
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
