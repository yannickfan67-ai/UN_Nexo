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
    private readonly Func<Task> _retry;
    private readonly TextBlock _stage;
    private readonly TextBlock _item;
    private readonly TextBlock _source;
    private readonly TextBlock _transfer;
    private readonly TextBlock _detail;
    private readonly TextBox _history;
    private readonly ProgressBar _progress;
    private readonly Button _cancel;
    private readonly Button _retryButton;
    private readonly Queue<string> _historyLines = new();
    private InstallProgress? _lastProgress;
    private string? _lastSource;
    private string? _lastHistoryMessage;
    private bool _sawActivity;
    private bool _cancelRequested;
    private bool _retryObservedStart;

    public DownloadManagerWindow(MinecraftVanillaInstallService installer, Func<Task> retry)
    {
        _installer = installer;
        _retry = retry;
        Title = "UN_Nexo Downloads";
        Width = 680;
        Height = 570;
        MinWidth = 540;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush.Parse("#0B0F14");

        _stage = new TextBlock { Text = "No active download", FontSize = 24, FontWeight = FontWeight.SemiBold };
        _item = Muted("Start or repair an instance to see transfer details here.", 13);
        _source = Muted("Source: —", 12);
        _transfer = new TextBlock { Text = "0 B / total unknown · 0 B/s", FontSize = 14 };
        _detail = Muted("Nexo keeps verified files and only fetches missing or invalid data.", 12);
        _detail.TextWrapping = TextWrapping.Wrap;
        _progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 8, Value = 0 };
        _cancel = new Button { Content = "Cancel active download", IsEnabled = installer.IsInstalling, Padding = new Thickness(14, 9) };
        _retryButton = new Button { Content = "Retry failed task", IsEnabled = false, Padding = new Thickness(14, 9) };
        _history = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 132,
            FontSize = 11,
            Text = "No failures or source switches in this session."
        };

        _cancel.Click += (_, _) =>
        {
            if (!_installer.CancelCurrentInstall())
                return;
            _cancelRequested = true;
            _cancel.IsEnabled = false;
            _detail.Text = "Cancellation requested. The current partial file will be discarded; verified files are kept.";
            AddHistory("Cancellation requested");
        };
        _retryButton.Click += async (_, _) => await RetryAsync();

        var header = new StackPanel { Spacing = 6 };
        header.Children.Add(new TextBlock { Text = "DOWNLOADS", Foreground = Brush.Parse("#8E9AAA"), FontSize = 11, FontWeight = FontWeight.SemiBold });
        header.Children.Add(_stage);
        header.Children.Add(_item);

        var stats = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        stats.Children.Add(_source);
        _transfer.SetValue(Grid.ColumnProperty, 1);
        stats.Children.Add(_transfer);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        actions.Children.Add(_cancel);
        actions.Children.Add(_retryButton);

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
        body.Children.Add(actions);
        body.Children.Add(new TextBlock { Text = "SESSION REPORT", Foreground = Brush.Parse("#8E9AAA"), FontSize = 11, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, 0) });
        body.Children.Add(_history);
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
        => Dispatcher.UIThread.Post(() => ApplyActivity(active));

    private void ApplyActivity(bool active)
    {
        _cancel.IsEnabled = active;
        if (active)
        {
            _sawActivity = true;
            _retryObservedStart = true;
            _cancelRequested = false;
            _retryButton.IsEnabled = false;
            _lastProgress = null;
            _lastSource = null;
            return;
        }

        if (!_sawActivity)
            return;

        _cancel.IsEnabled = false;
        var last = _lastProgress;
        if (last?.Stage == "Ready" || _progress.Value >= 100)
        {
            _stage.Text = "Download complete";
            _detail.Text = "All required Vanilla files are ready. Verified files remain cached for future repairs or retries.";
            _retryButton.IsEnabled = false;
            AddHistory($"Completed successfully{FormatItemSuffix(last?.CurrentItem)}");
        }
        else if (_cancelRequested)
        {
            _stage.Text = "Download cancelled";
            _detail.Text = "Cancelled by user. Retry resumes by reusing every file that already passed verification.";
            _retryButton.IsEnabled = true;
            AddHistory($"Cancelled{FormatItemSuffix(last?.CurrentItem)}");
        }
        else
        {
            _stage.Text = "Download failed";
            var reason = FailureReason(last);
            _detail.Text = reason;
            _retryButton.IsEnabled = true;
            AddHistory($"Failed{FormatItemSuffix(last?.CurrentItem)} · {reason}");
        }

        _sawActivity = false;
    }

    private void OnProgressChanged(InstallProgress value)
        => Dispatcher.UIThread.Post(() => ApplyProgress(value));

    private void ApplyProgress(InstallProgress value)
    {
        _lastProgress = value;
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

        if (value.IsFallback && !string.Equals(_lastSource, value.Source, StringComparison.Ordinal))
            AddHistory($"Fallback to {value.Source ?? "alternate source"}{FormatItemSuffix(value.CurrentItem)}");
        if (LooksLikeFailure(value.Detail))
            AddHistory($"{value.Detail}{FormatItemSuffix(value.CurrentItem)}");
        _lastSource = value.Source;
    }

    private async Task RetryAsync()
    {
        if (_installer.IsInstalling)
            return;

        _retryButton.IsEnabled = false;
        _retryObservedStart = false;
        _cancelRequested = false;
        _progress.Value = 0;
        _stage.Text = "Retrying…";
        _detail.Text = "Re-running preparation. Files that already passed SHA-1 verification will be reused.";
        AddHistory("Retry requested");

        try
        {
            await _retry();
        }
        catch (Exception ex)
        {
            _stage.Text = "Retry failed to start";
            _detail.Text = ex.Message;
            _retryButton.IsEnabled = true;
            AddHistory($"Retry launcher error · {ex.Message}");
            return;
        }

        if (!_retryObservedStart)
        {
            _stage.Text = "Retry did not start";
            _detail.Text = "Select the failed instance and make sure no game or install task is already running.";
            _retryButton.IsEnabled = true;
            AddHistory("Retry did not start");
        }
    }

    private void AddHistory(string message)
    {
        message = message.Trim();
        if (message.Length == 0 || string.Equals(message, _lastHistoryMessage, StringComparison.Ordinal))
            return;

        _lastHistoryMessage = message;
        _historyLines.Enqueue($"{DateTime.Now:HH:mm:ss}  {message}");
        while (_historyLines.Count > 9)
            _historyLines.Dequeue();
        _history.Text = string.Join(Environment.NewLine, _historyLines);
    }

    private static bool LooksLikeFailure(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return false;
        return detail.Contains("HTTP ", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("no progress", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("Switching source", StringComparison.OrdinalIgnoreCase);
    }

    private static string FailureReason(InstallProgress? last)
    {
        if (!string.IsNullOrWhiteSpace(last?.Detail)
            && !string.Equals(last.Detail, "Downloading", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(last.Detail, "Connecting…", StringComparison.OrdinalIgnoreCase))
            return last.Detail;
        return "The task stopped before all required files were ready. Retry will keep verified files and fetch only missing or invalid data.";
    }

    private static string FormatItemSuffix(string? item)
        => string.IsNullOrWhiteSpace(item) ? string.Empty : $" · {item}";

    private static string FormatTransfer(long bytes, long? total, double bytesPerSecond)
    {
        var size = total is > 0
            ? $"{FormatBytes(bytes)} / {FormatBytes(total.Value)}"
            : $"{FormatBytes(bytes)} / total unknown";
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
