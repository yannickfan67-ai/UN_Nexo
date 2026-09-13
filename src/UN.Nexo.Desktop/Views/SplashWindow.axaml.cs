using Avalonia.Controls;

namespace UN.Nexo.Desktop.Views;

public sealed partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        Opacity = 0;
    }

    public Task FadeInAsync()
        => AnimateOpacityAsync(0, 1, 9, 18);

    public async Task FadeOutAndCloseAsync()
    {
        await AnimateOpacityAsync(Opacity, 0, 8, 16);
        Close();
    }

    private async Task AnimateOpacityAsync(double from, double to, int frames, int delayMs)
    {
        for (var frame = 0; frame <= frames; frame++)
        {
            var progress = frame / (double)frames;
            Opacity = from + ((to - from) * progress);
            await Task.Delay(delayMs);
        }
        Opacity = to;
    }
}
