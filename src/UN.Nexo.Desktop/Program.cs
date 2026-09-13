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
