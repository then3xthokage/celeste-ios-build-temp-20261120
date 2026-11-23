using System;
using System.IO;
using System.Threading.Tasks;
using Foundation;
using UIKit;

namespace IOSInterpreterSmoke;

[Register("AppDelegate")]
internal sealed class AppDelegate : UIApplicationDelegate
{
    private UIWindow window;

    public override bool FinishedLaunching(UIApplication application, NSDictionary launchOptions)
    {
        window = new UIWindow(UIScreen.MainScreen.Bounds)
        {
            RootViewController = new UIViewController()
        };
        window.MakeKeyAndVisible();

        string resultPath = Program.ResultPath;
        Program.WriteProgress("RUNNING: app delegate entered");
        RuntimeProbe.Progress = stage => Program.WriteProgress("RUNNING: " + stage);
        Task.Run(() =>
        {
            try
            {
                RuntimeProbe.Run();
                Program.WriteProgress("PASS");
            }
            catch (Exception exception)
            {
                Program.WriteProgress("FAIL at " + RuntimeProbe.CurrentStage + "\n" + exception);
            }
        });
        return true;
    }
}
