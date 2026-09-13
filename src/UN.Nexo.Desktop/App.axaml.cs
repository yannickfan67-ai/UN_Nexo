using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using UN.Nexo.Desktop.Diagnostics;
using UN.Nexo.Desktop.Views;

namespace UN.Nexo.Desktop;

public sealed partial class App : Application
{
    private static readonly TimeSpan SplashInitializationLimit = TimeSpan.FromSeconds(5);

    public override void Initialize()
    {
        LauncherStartupTrace.Write("[startup] App.Initialize · loading Avalonia XAML");
        AvaloniaXamlLoader.Load(this);
        LauncherStartupTrace.Write("[startup] App.Initialize · XAML loaded");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        LauncherStartupTrace.Write("[startup] Framework initialization completed");

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            LauncherStartupTrace.Write("[startup] Creating splash window");
            var splash = new SplashWindow();
            desktop.MainWindow = splash;
            splash.Show();
            LauncherStartupTrace.Write("[startup] Splash window shown");
            _ = StartDesktopAsync(desktop, splash);
        }
        else
        {
            LauncherStartupTrace.Write("[startup] Classic desktop lifetime unavailable");
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task StartDesktopAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        SplashWindow splash)
    {
        var started = Stopwatch.StartNew();
        MainWindow main;

        try
        {
            LauncherStartupTrace.Write("[startup] Constructing MainWindow");
            main = new MainWindow();
            LauncherStartupTrace.Write("[startup] MainWindow constructed");
        }
        catch (Exception ex)
        {
            LauncherStartupTrace.Failure("MainWindow construction", ex);
            throw;
        }

        LauncherStartupTrace.Write("[startup] Starting MainWindow.InitializeAsync");
        var initializationTask = main.InitializeAsync();
        var splashFadeTask = splash.FadeInAsync();

        try
        {
            await splashFadeTask;
            LauncherStartupTrace.Write("[startup] Splash fade-in completed");
        }
        catch (Exception ex)
        {
            LauncherStartupTrace.Failure("splash fade-in", ex);
        }

        var completed = await Task.WhenAny(initializationTask, Task.Delay(SplashInitializationLimit));
        if (completed == initializationTask)
        {
            try
            {
                await initializationTask;
                LauncherStartupTrace.Write($"[startup] MainWindow initialization completed in {started.ElapsedMilliseconds} ms");
            }
            catch (Exception ex)
            {
                LauncherStartupTrace.Failure("MainWindow initialization", ex);
            }
        }
        else
        {
            LauncherStartupTrace.Write($"[startup] MainWindow initialization exceeded {SplashInitializationLimit.TotalSeconds:0} s · continuing to UI");
            _ = ObserveLateInitializationAsync(initializationTask);
        }

        var remaining = TimeSpan.FromMilliseconds(620) - started.Elapsed;
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining);

        try
        {
            LauncherStartupTrace.Write("[startup] Showing MainWindow");
            main.Opacity = 0;
            desktop.MainWindow = main;
            main.Show();

            await splash.FadeOutAndCloseAsync();
            LauncherStartupTrace.Write("[startup] Splash closed");
            await main.RevealAsync();
            LauncherStartupTrace.Complete();
        }
        catch (Exception ex)
        {
            LauncherStartupTrace.Failure("main window reveal", ex);
            throw;
        }
    }

    private static async Task ObserveLateInitializationAsync(Task initializationTask)
    {
        try
        {
            await initializationTask;
            LauncherStartupTrace.Write("[startup] Deferred MainWindow initialization completed");
        }
        catch (Exception ex)
        {
            LauncherStartupTrace.Failure("deferred MainWindow initialization", ex);
        }
    }
}
