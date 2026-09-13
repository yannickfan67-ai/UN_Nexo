using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;
using UN.Nexo.Desktop.ViewModels;

namespace UN.Nexo.Desktop.Views;

public sealed partial class InstanceRepairWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly NexoPathService _paths;
    private readonly DownloadSourceService _downloadSources;
    private readonly LauncherSettingsService _settings;
    private readonly MinecraftInstanceRepairService _repairService;
    private CancellationTokenSource? _operation;
    private bool _isBusy;

    public InstanceRepairWindow(MainWindowViewModel viewModel, NexoPathService paths)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _paths = paths;
        _downloadSources = new DownloadSourceService();
        _settings = new LauncherSettingsService(paths);

        var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("UN_Nexo/repair");
        var installer = new MinecraftVanillaInstallService(httpClient, paths, _downloadSources);
        var manifest = new MinecraftVersionManifestService(httpClient, _downloadSources);
        _repairService = new MinecraftInstanceRepairService(
            paths,
            installer,
            manifest,
            new JavaDiscoveryService(paths),
            new MinecraftRuntimeInspector(paths),
            new JavaRuntimeProvisionService(paths));

        Opened += OnOpened;
        Closed += OnClosed;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateInstanceHeader();
        UpdateButtons();
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        try
        {
            var settings = await _settings.LoadAsync();
            _downloadSources.SetSource(settings.DownloadSource);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Could not load launcher settings: {ex.Message}";
        }

        if (_viewModel.SelectedInstance is not null)
            await RunCheckAsync();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _operation?.Cancel();
        _operation?.Dispose();
        _operation = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedInstance)
            || e.PropertyName == nameof(MainWindowViewModel.IsGameRunning))
        {
            UpdateInstanceHeader();
            IssuesList.ItemsSource = null;
            FindingCount.Text = "0";
            SummaryText.Text = "Run Check to inspect this instance.";
            StatusText.Text = _viewModel.IsGameRunning
                ? "Stop the running game before repairing files."
                : "Waiting.";
            RepairProgress.Value = 0;
            UpdateButtons();
        }
    }

    private void UpdateInstanceHeader()
    {
        var instance = _viewModel.SelectedInstance;
        if (instance is null)
        {
            InstanceName.Text = "No instance selected";
            InstanceVersion.Text = "Select an instance in the main window.";
            return;
        }

        InstanceName.Text = instance.Name;
        InstanceVersion.Text = $"Minecraft {instance.VersionId} · {instance.Loader}";
    }

    private void UpdateButtons()
    {
        var hasInstance = _viewModel.SelectedInstance is not null;
        CheckButton.IsEnabled = hasInstance && !_isBusy;
        RepairButton.IsEnabled = hasInstance && !_isBusy && !_viewModel.IsGameRunning;
        CancelButton.IsVisible = _isBusy;
        CancelButton.IsEnabled = _isBusy;
    }

    private async void OnCheckClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await RunCheckAsync();

    private async void OnRepairClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var instance = _viewModel.SelectedInstance;
        if (instance is null)
        {
            StatusText.Text = "Select an instance first.";
            return;
        }
        if (_viewModel.IsGameRunning)
        {
            StatusText.Text = "Stop the running game before repairing its files.";
            return;
        }

        await RunOperationAsync(async token =>
        {
            RepairProgress.IsIndeterminate = false;
            RepairProgress.Value = 0;
            SummaryText.Text = $"Repairing {instance.Name}…";

            var installProgress = new Progress<InstallProgress>(value =>
            {
                RepairProgress.IsIndeterminate = value.Total <= 0;
                if (value.Total > 0)
                    RepairProgress.Value = value.Percent;
                StatusText.Text = value.Total > 0
                    ? $"{value.Stage} · {value.Completed}/{value.Total}{(string.IsNullOrWhiteSpace(value.CurrentItem) ? string.Empty : $" · {value.CurrentItem}")}"
                    : value.Stage;
            });
            var statusProgress = new Progress<string>(message => StatusText.Text = message);

            var report = await _repairService.RepairAsync(
                instance,
                installProgress,
                statusProgress,
                token);
            PresentReport(report);
            RepairProgress.IsIndeterminate = false;
            RepairProgress.Value = report.ErrorCount == 0 ? 100 : RepairProgress.Value;
        });
    }

    private async Task RunCheckAsync()
    {
        var instance = _viewModel.SelectedInstance;
        if (instance is null)
        {
            StatusText.Text = "Select an instance first.";
            return;
        }

        await RunOperationAsync(async token =>
        {
            SummaryText.Text = $"Checking {instance.Name}…";
            RepairProgress.IsIndeterminate = true;
            var progress = new Progress<string>(message => StatusText.Text = message);
            var report = await _repairService.CheckAsync(instance, progress, token);
            PresentReport(report);
            RepairProgress.IsIndeterminate = false;
            RepairProgress.Value = 0;
        });
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> action)
    {
        if (_isBusy)
            return;

        _operation?.Dispose();
        _operation = new CancellationTokenSource();
        _isBusy = true;
        UpdateButtons();

        try
        {
            await action(_operation.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Operation cancelled.";
            SummaryText.Text = "No files were intentionally removed.";
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or HttpRequestException
            or InvalidDataException
            or InvalidOperationException
            or PlatformNotSupportedException)
        {
            StatusText.Text = ex.Message;
            SummaryText.Text = "Check or repair could not finish.";
        }
        finally
        {
            _isBusy = false;
            RepairProgress.IsIndeterminate = false;
            UpdateButtons();
        }
    }

    private void PresentReport(InstanceHealthReport report)
    {
        IssuesList.ItemsSource = report.Issues;
        FindingCount.Text = $"{report.Issues.Count} finding{(report.Issues.Count == 1 ? string.Empty : "s")}";
        SummaryText.Text = report.Summary;
        StatusText.Text = report.RequiredJavaMajor is null
            ? $"Checked {report.CheckedAt.LocalDateTime:g}. Java requirement unavailable until version metadata is present."
            : $"Checked {report.CheckedAt.LocalDateTime:g} · requires 64-bit Java {report.RequiredJavaMajor}.";
    }

    private void OnCancelClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _operation?.Cancel();

    private void OnOpenGameFolderClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var instance = _viewModel.SelectedInstance;
        if (instance is null)
        {
            StatusText.Text = "Select an instance first.";
            return;
        }

        var path = _paths.GetInstanceGameDirectory(instance.Id);
        try
        {
            Directory.CreateDirectory(path);
            var info = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "explorer.exe"
                    : OperatingSystem.IsMacOS() ? "open" : "xdg-open",
                UseShellExecute = false
            };
            info.ArgumentList.Add(path);
            Process.Start(info);
            StatusText.Text = $"Opened {path}";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            StatusText.Text = $"Could not open folder: {ex.Message}";
        }
    }
}
