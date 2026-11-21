using System;
using System.Reflection;
using System.IO;
using SDL2;

namespace celestemeow
{
    public class Application
    {

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
                SDL2.SDL.SDL_SetHint(SDL.SDL_HINT_MOUSE_TOUCH_EVENTS, "0");
                SDL2.SDL.SDL_SetHint(SDL.SDL_HINT_TOUCH_MOUSE_EVENTS, "0");

                Console.WriteLine("setting SDL hint FNA3D_FORCE_DRIVER to Metal");
                SDL2.SDL.SDL_SetHint("FNA3D_FORCE_DRIVER", "Metal");

                realArgs = args;
                SDL2.SDL.SDL_UIKitRunApp(0, IntPtr.Zero, FakeMain);
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
            try
            {
                RealMain(realArgs);
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
            Console.WriteLine("hello hi :3");

            // thanks to pixel#0772 on the celeste discord server for this <3
            //Type MeWhenIStealCodeFromAScreenshotInTheCelesteDiscordServer = typeof(UIWindow).GetType();
            MethodInfo method = typeof(Celeste.Celeste).GetMethod("Main", BindingFlags.NonPublic | BindingFlags.Static);
            method.Invoke(null, new object[] { args });
        }
    }
}
