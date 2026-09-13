using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Desktop.Views;

public sealed class DownloadManagerWindow : Window
{
    private readonly MinecraftVanillaInstallService _installer;
    private readonly TextBlock _stage;
    private readonly TextBlock _item;
    private readonly TextBlock _source;
    private readonly TextBlock _transfer;
    private readonly TextBlock _detail;
    private readonly ProgressBar _progress;
    private readonly Button _cancel;

    public DownloadManagerWindow(MinecraftVanillaInstallService installer)
    {
        _installer = installer;
        Title = "UN_Nexo Downloads";
        Width = 620;
        Height = 430;
        MinWidth = 520;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush.Parse("#0B0F14");

        _stage = new TextBlock { Text = "No active download", FontSize = 24, FontWeight = FontWeight.SemiBold };
        _item = Muted("Start or repair an instance to see transfer details here.", 13);
        _source = Muted("Source: —", 12);
        _transfer = new TextBlock { Text = "0 B · 0 B/s", FontSize = 14 };
        _detail = Muted("Nexo keeps verified files and only fetches missing or invalid data.", 12);
        _detail.TextWrapping = TextWrapping.Wrap;
        _progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 8, Value = 0 };
        _cancel = new Button { Content = "Cancel active download", IsEnabled = installer.IsInstalling, Padding = new Thickness(14, 9) };
        _cancel.Click += (_, _) => _installer.CancelCurrentInstall();

        var header = new StackPanel { Spacing = 6 };
        header.Children.Add(new TextBlock { Text = "DOWNLOADS", Foreground = Brush.Parse("#8E9AAA"), FontSize = 11, FontWeight = FontWeight.SemiBold });
        header.Children.Add(_stage);
        header.Children.Add(_item);

        var stats = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        stats.Children.Add(_source);
        _transfer.SetValue(Grid.ColumnProperty, 1);
        stats.Children.Add(_transfer);

        var card = new Border
        {
            Background = Brush.Parse("#121923"),
            BorderBrush = Brush.Parse("#202B38"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(18)
        };
        var body = new StackPanel { Spacing = 14 };
        body.Children.Add(header);
        body.Children.Add(_progress);
        body.Children.Add(stats);
        body.Children.Add(_detail);
        body.Children.Add(_cancel);
        card.Child = body;

        Content = new Border { Padding = new Thickness(28), Child = card };

        _installer.ProgressChanged += OnProgressChanged;
        _installer.InstallActivityChanged += OnInstallActivityChanged;
        Closed += (_, _) =>
        {
            _installer.ProgressChanged -= OnProgressChanged;
            _installer.InstallActivityChanged -= OnInstallActivityChanged;
        };
    }

    private static TextBlock Muted(string text, double size)
        => new() { Text = text, Foreground = Brush.Parse("#8E9AAA"), FontSize = size };

    private void OnInstallActivityChanged(bool active)
        => Dispatcher.UIThread.Post(() =>
        {
            _cancel.IsEnabled = active;
            if (!active && _progress.Value < 100)
                _stage.Text = "No active download";
        });

    private void OnProgressChanged(InstallProgress value)
        => Dispatcher.UIThread.Post(() => ApplyProgress(value));

    private void ApplyProgress(InstallProgress value)
    {
        _stage.Text = value.Stage;
        _item.Text = string.IsNullOrWhiteSpace(value.CurrentItem) ? "Preparing files…" : value.CurrentItem;
        _source.Text = $"Source: {value.Source ?? "—"}{(value.IsFallback ? " · fallback" : string.Empty)}";
        _progress.Value = value.Percent;
        _transfer.Text = FormatTransfer(value.BytesDownloaded, value.TotalBytes, value.BytesPerSecond);
        _detail.Text = !string.IsNullOrWhiteSpace(value.Detail)
            ? value.Detail
            : value.Total > 0
                ? $"{Math.Clamp(value.Completed, 0, value.Total)}/{value.Total} files complete"
                : "Working…";
        _cancel.IsEnabled = _installer.IsInstalling;
    }

    private static string FormatTransfer(long bytes, long? total, double bytesPerSecond)
    {
        var size = total is > 0 ? $"{FormatBytes(bytes)} / {FormatBytes(total.Value)}" : FormatBytes(bytes);
        return $"{size} · {FormatBytes((long)Math.Max(0, bytesPerSecond))}/s";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB"];
        var value = Math.Max(0, bytes);
        var scaled = (double)value;
        var unit = 0;
        while (scaled >= 1024 && unit < units.Length - 1)
        {
            scaled /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value} {units[unit]}" : $"{scaled:0.0} {units[unit]}";
    }
}
