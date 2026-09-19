using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls;
using UN.Nexo.Core.Launching;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;
using UN.Nexo.Desktop.ViewModels;

namespace UN.Nexo.Desktop.Views;

public sealed partial class RuntimeSettingsWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly NexoPathService _paths;
    private readonly LauncherRuntimeSettingsService _settings;
    private readonly MinecraftRuntimeInspector _inspector;
    private bool _settingsLoadFailed;

    public RuntimeSettingsWindow(MainWindowViewModel viewModel, NexoPathService paths)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _paths = paths;
        _settings = new LauncherRuntimeSettingsService(paths);
        _inspector = new MinecraftRuntimeInspector(paths);
        JavaList.ItemsSource = viewModel.JavaInstallations;
        Opened += OnOpened;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += (_, _) => _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        string? loadFailure = null;
        try
        {
            var settings = await _settings.LoadAsync();
            _settingsLoadFailed = false;
            MemoryBox.Value = settings.MemoryMb;
            JvmArgumentsBox.Text = settings.ExtraJvmArguments;
        }
        catch (Exception ex) when (
            ex is InvalidDataException
            or IOException
            or UnauthorizedAccessException)
        {
            _settingsLoadFailed = true;
            loadFailure =
                $"runtime-settings.json could not be loaded: {ex.Message} The existing file was preserved and Save is disabled until it is recovered.";
        }

        UpdateMemoryRecommendation();
        await UpdateInstanceDiagnosticsAsync();
        StatusText.Text = loadFailure
            ?? "Runtime settings apply to the next game launch.";
    }

    private async void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedInstance))
            await UpdateInstanceDiagnosticsAsync();
    }

    private void UpdateMemoryRecommendation()
    {
        var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var recommended = RuntimeLaunchOptions.RecommendMemoryMb(available);
        var availableGiB = available > 0 ? available / 1024d / 1024d / 1024d : 0;
        MemoryRecommendation.Text = available > 0
            ? $"Auto currently resolves to {recommended} MB from about {availableGiB:0.0} GiB available to this process."
            : $"Auto currently resolves to {recommended} MB.";
    }

    private async Task UpdateInstanceDiagnosticsAsync()
    {
        var instance = _viewModel.SelectedInstance;
        if (instance is null)
        {
            InstanceSummary.Text = "No instance selected.";
            JavaRequirement.Text = "Select an instance on the main window to inspect its runtime requirement.";
            return;
        }

        InstanceSummary.Text = $"{instance.Name} · Minecraft {instance.VersionId}";
        var required = await _inspector.GetRequiredJavaMajorAsync(instance);
        if (required is null)
        {
            JavaRequirement.Text = "Version metadata is not prepared yet, so the required Java cannot be inspected.";
            return;
        }

        var match = _viewModel.JavaInstallations.FirstOrDefault(java =>
            java.Is64Bit && MinecraftLaunchPlanBuilder.JavaMajor(java.Version) == required.Value);
        JavaRequirement.Text = match is null
            ? $"Requires 64-bit Java {required}; no matching runtime is currently detected."
            : $"Requires Java {required} · matched {match.Version} at {match.JavaPath}";
    }

    private async void OnSaveClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_settingsLoadFailed)
        {
            StatusText.Text =
                "Runtime settings were not saved because the existing file could not be loaded. Recover or explicitly reset runtime-settings.json first.";
            return;
        }

        try
        {
            var memory = (int)(MemoryBox.Value ?? 0);
            var settings = new LauncherRuntimeSettings(memory, JvmArgumentsBox.Text?.Trim() ?? string.Empty);
            await _settings.SaveAsync(settings);
            var effective = memory == 0
                ? RuntimeLaunchOptions.RecommendMemoryMb(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes)
                : memory;
            StatusText.Text = $"Saved. Next launch will use up to {effective} MB plus the validated JVM options.";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void OnUseAutoClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        MemoryBox.Value = 0;
        UpdateMemoryRecommendation();
        StatusText.Text = "Auto memory selected. Save to apply it.";
    }

    private void OnOpenGameFolderClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var instance = _viewModel.SelectedInstance;
        if (instance is null)
        {
            StatusText.Text = "Select an instance first.";
            return;
        }
        OpenDirectory(_paths.GetInstanceGameDirectory(instance.Id));
    }

    private void OnOpenLogsClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var instance = _viewModel.SelectedInstance;
        if (instance is null)
        {
            StatusText.Text = "Select an instance first.";
            return;
        }
        OpenDirectory(Path.Combine(_paths.GetInstanceDirectory(instance.Id), "launcher-logs"));
    }

    private void OpenDirectory(string path)
    {
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
