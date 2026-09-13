using Avalonia;
using UN.Nexo.Desktop.Diagnostics;

namespace UN.Nexo.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        LauncherStartupTrace.Start();
        LauncherStartupTrace.Write($"[startup] Program.Main entered · args={args.Length}");

        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception)
                LauncherStartupTrace.Failure("unhandled AppDomain exception", exception);
            else
                LauncherStartupTrace.Write($"[failure] Unhandled AppDomain object: {eventArgs.ExceptionObject}");
        };
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            LauncherStartupTrace.Failure("unobserved task exception", eventArgs.Exception);
        };

        try
        {
            LauncherStartupTrace.Write("[startup] Building Avalonia application");
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            LauncherStartupTrace.Write("[startup] Avalonia desktop lifetime exited normally");
        }
        catch (Exception ex)
        {
            LauncherStartupTrace.Failure("desktop lifetime", ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont();
}
