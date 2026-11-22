using System;
using System.IO;
using UIKit;

namespace IOSInterpreterSmoke;

internal static class Program
{
    public static string ResultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ios-interpreter-smoke.txt");

    public static void WriteProgress(string message)
    {
        string directory = Path.GetDirectoryName(ResultPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(ResultPath, message);
    }

    private static void Main(string[] args)
    {
        WriteProgress("RUNNING: managed entry point");
        UIApplication.Main(args, null, "AppDelegate");
    }
}
