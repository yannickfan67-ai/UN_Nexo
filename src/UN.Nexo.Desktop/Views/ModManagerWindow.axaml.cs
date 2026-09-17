using Avalonia.Controls;
using Avalonia.Controls.Selection;
using Avalonia.Platform.Storage;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;
using UN.Nexo.Desktop.ViewModels;

namespace UN.Nexo.Desktop.Views;

public sealed partial class ModManagerWindow : Window
{
    private readonly MainWindowViewModel? _viewModel;
    private readonly NexoPathService _paths;
    private readonly InstanceModService _mods;
    private bool _busy;

    public ModManagerWindow()
    {
        InitializeComponent();
        _paths = new NexoPathService();
        _mods = new InstanceModService(_paths);
        Opened += (_, _) => RefreshInstances();
    }

    public ModManagerWindow(MainWindowViewModel viewModel, NexoPathService paths) : this()
    {
        _viewModel = viewModel;
        _paths = paths;
        _mods = new InstanceModService(paths);
    }

    private GameInstance? SelectedInstance => InstanceBox.SelectedItem as GameInstance;
    private InstalledMod? SelectedMod => ModsList.SelectedItem as InstalledMod;

    private void RefreshInstances()
    {
        var selectedId = SelectedInstance?.Id ?? _viewModel?.SelectedInstance?.Id;
        var instances = _viewModel?.Instances.ToArray() ?? [];
        InstanceBox.ItemsSource = instances;
        InstanceBox.SelectedItem = instances.FirstOrDefault(instance => instance.Id == selectedId)
            ?? instances.FirstOrDefault(instance => string.Equals(instance.Loader, "fabric", StringComparison.OrdinalIgnoreCase))
            ?? instances.FirstOrDefault();
        RefreshMods();
    }

    private void RefreshMods()
    {
        var instance = SelectedInstance;
        if (instance is null)
        {
            ModsList.ItemsSource = Array.Empty<InstalledMod>();
            InstanceDetail.Text = "Choose a Fabric instance.";
            ModsDirectoryLabel.Text = "No instance selected.";
            CountLabel.Text = "0 mods";
            InstallButton.IsEnabled = false;
            ToggleButton.IsEnabled = false;
            RemoveButton.IsEnabled = false;
            return;
        }

        var isFabric = string.Equals(instance.Loader, "fabric", StringComparison.OrdinalIgnoreCase);
        InstanceDetail.Text = $"{instance.Name} · {instance.MinecraftVersionId} · {instance.Loader}" +
            (isFabric ? string.Empty : " · Local mod installation is currently enabled for Fabric instances only.");
        ModsDirectoryLabel.Text = _mods.GetModsDirectory(instance.Id);

        try
        {
            var mods = _mods.List(instance.Id);
            ModsList.ItemsSource = mods;
            CountLabel.Text = $"{mods.Count} mod{(mods.Count == 1 ? string.Empty : "s")}";
            InstallButton.IsEnabled = !_busy && isFabric && _viewModel?.IsGameRunning != true;
            UpdateSelectionButtons();
        }
        catch (Exception ex)
        {
            ModsList.ItemsSource = Array.Empty<InstalledMod>();
            CountLabel.Text = "0 mods";
            InstallButton.IsEnabled = false;
            ToggleButton.IsEnabled = false;
            RemoveButton.IsEnabled = false;
            OperationStatus.Text = $"Could not read mods: {ex.Message}";
        }
    }

    private void OnInstanceSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is not null && SelectedInstance is not null)
            _viewModel.SelectedInstance = SelectedInstance;
        RefreshMods();
    }

    private void OnModSelectionChanged(object? sender, SelectionChangedEventArgs e)
        => UpdateSelectionButtons();

    private void UpdateSelectionButtons()
    {
        var selected = SelectedMod;
        var canModify = !_busy && selected is not null && _viewModel?.IsGameRunning != true;
        ToggleButton.IsEnabled = canModify;
        RemoveButton.IsEnabled = canModify;
        ToggleButton.Content = selected?.IsEnabled == false ? "Enable selected" : "Disable selected";
    }

    private void OnRefreshClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => RefreshInstances();

    private async void OnInstallClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_busy || SelectedInstance is not { } instance)
            return;
        if (_viewModel?.IsGameRunning == true)
        {
            OperationStatus.Text = "Stop Minecraft before changing the instance mod set.";
            RefreshMods();
            return;
        }
        if (!string.Equals(instance.Loader, "fabric", StringComparison.OrdinalIgnoreCase))
        {
            OperationStatus.Text = "Local mod installation is currently available for Fabric instances only.";
            return;
        }
        if (!StorageProvider.CanOpen)
        {
            OperationStatus.Text = "This platform does not expose a file picker. Copy .jar files into the shown mods directory manually.";
            return;
        }

        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = $"Install mods into {instance.Name}",
                AllowMultiple = true,
                FileTypeFilter =
                [
                    new FilePickerFileType("Minecraft mod JAR")
                    {
                        Patterns = ["*.jar"]
                    }
                ]
            });
            if (files.Count == 0)
                return;

            _busy = true;
            RefreshMods();
            var installed = 0;
            foreach (var file in files)
            {
                var localPath = file.TryGetLocalPath();
                if (string.IsNullOrWhiteSpace(localPath))
                    throw new IOException($"'{file.Name}' does not expose a local filesystem path.");
                await _mods.InstallAsync(instance.Id, localPath, replaceExisting: true);
                installed++;
            }
            OperationStatus.Text = $"Installed {installed} mod{(installed == 1 ? string.Empty : "s")} into '{instance.Name}'.";
        }
        catch (Exception ex)
        {
            OperationStatus.Text = $"Mod installation failed: {ex.Message}";
        }
        finally
        {
            _busy = false;
            RefreshMods();
        }
    }

    private void OnToggleClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_busy || SelectedInstance is not { } instance || SelectedMod is not { } mod)
            return;
        if (_viewModel?.IsGameRunning == true)
        {
            OperationStatus.Text = "Stop Minecraft before changing the instance mod set.";
            RefreshMods();
            return;
        }

        try
        {
            _mods.SetEnabled(instance.Id, mod.FileName, !mod.IsEnabled);
            OperationStatus.Text = $"{(mod.IsEnabled ? "Disabled" : "Enabled")} '{mod.DisplayName}'.";
        }
        catch (Exception ex)
        {
            OperationStatus.Text = $"Could not change mod state: {ex.Message}";
        }
        RefreshMods();
    }

    private void OnRemoveClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_busy || SelectedInstance is not { } instance || SelectedMod is not { } mod)
            return;
        if (_viewModel?.IsGameRunning == true)
        {
            OperationStatus.Text = "Stop Minecraft before changing the instance mod set.";
            RefreshMods();
            return;
        }

        try
        {
            _mods.Remove(instance.Id, mod.FileName);
            OperationStatus.Text = $"Removed '{mod.DisplayName}' from '{instance.Name}'.";
        }
        catch (Exception ex)
        {
            OperationStatus.Text = $"Could not remove mod: {ex.Message}";
        }
        RefreshMods();
    }
}
