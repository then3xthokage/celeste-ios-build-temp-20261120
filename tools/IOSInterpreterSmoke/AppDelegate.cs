using System;
using System.IO;
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

        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        Directory.CreateDirectory(documents);
        string resultPath = Path.Combine(documents, "ios-interpreter-smoke.txt");
        try
        {
            RuntimeProbe.Run();
            File.WriteAllText(resultPath, "PASS");
        }
        catch (Exception exception)
        {
            File.WriteAllText(resultPath, "FAIL\n" + exception);
        }
        return true;
    }
}
