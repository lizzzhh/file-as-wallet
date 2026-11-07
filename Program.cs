using System;
using System.Linq;
using Avalonia;
using FileAsWallet.Services;

namespace FileAsWallet;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called.
    [STAThread]
    public static void Main(string[] args)
    {
        // 开发自检：FileAsWallet --selftest
        if (args.Contains("--selftest"))
        {
            Environment.ExitCode = SelfTest.Run();
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
