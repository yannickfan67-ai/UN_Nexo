using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using UN.Nexo.Desktop.Views;

namespace UN.Nexo.Desktop;

public sealed partial class App : Application
{
    public override void Initialize()
        => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var splash = new SplashWindow();
            desktop.MainWindow = splash;
            splash.Show();
            _ = StartDesktopAsync(desktop, splash);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task StartDesktopAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        SplashWindow splash)
    {
        var started = Stopwatch.StartNew();
        var main = new MainWindow();

        try
        {
            await Task.WhenAll(splash.FadeInAsync(), main.InitializeAsync());
        }
        catch
        {
            // The main window owns user-facing initialization errors. Keep startup recoverable.
        }

        var remaining = TimeSpan.FromMilliseconds(620) - started.Elapsed;
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining);

        main.Opacity = 0;
        desktop.MainWindow = main;
        main.Show();

        await splash.FadeOutAndCloseAsync();
        await main.RevealAsync();
    }
}
