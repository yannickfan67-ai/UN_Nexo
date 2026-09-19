using System.Diagnostics;
using System.Reflection;
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
    private readonly HttpClient _modrinthHttpClient;
    private readonly IModProvider _modrinth;
    private ModProviderVersion? _selectedModrinthVersion;
    private int _modrinthSelectionGeneration;
    private bool _busy;

    public ModManagerWindow()
    {
        InitializeComponent();
        _paths = new NexoPathService();
        _mods = new InstanceModService(_paths);
        _modrinthHttpClient = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false
        })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _modrinth = new ModrinthModProvider(_modrinthHttpClient, BuildModrinthUserAgent());
        Opened += (_, _) => RefreshInstances();
        Closed += (_, _) => _modrinthHttpClient.Dispose();
    }

    public ModManagerWindow(MainWindowViewModel viewModel, NexoPathService paths) : this()
    {
        _viewModel = viewModel;
        _paths = paths;
        _mods = new InstanceModService(paths);
    }

    private GameInstance? SelectedInstance => InstanceBox.SelectedItem as GameInstance;
    private InstalledMod? SelectedMod => ModsList.SelectedItem as InstalledMod;
    private ModrinthBrowserItem? SelectedModrinthItem => ModrinthResultsList.SelectedItem as ModrinthBrowserItem;

    private void RefreshInstances()
    {
        var selectedId = SelectedInstance?.Id ?? _viewModel?.SelectedInstance?.Id;
        var instances = _viewModel?.Instances.ToArray() ?? [];
        InstanceBox.ItemsSource = instances;
        InstanceBox.SelectedItem = instances.FirstOrDefault(instance => instance.Id == selectedId)
            ?? instances.FirstOrDefault(instance => string.Equals(instance.Loader, "fabric", StringComparison.OrdinalIgnoreCase))
            ?? instances.FirstOrDefault();
        RefreshMods();
        ResetModrinthResults();
    }

    private void RefreshMods()
    {
        var instance = SelectedInstance;
        if (instance is null)
        {
            ModsList.ItemsSource = Array.Empty<InstalledMod>();
            InstanceDetail.Text = "Choose an instance.";
            ModsDirectoryLabel.Text = "No instance selected.";
            CountLabel.Text = "0 mods";
            InstallButton.IsEnabled = false;
            ToggleButton.IsEnabled = false;
            RemoveButton.IsEnabled = false;
            UpdateModrinthAvailability();
            return;
        }

        var isFabric = string.Equals(instance.Loader, "fabric", StringComparison.OrdinalIgnoreCase);
        InstanceDetail.Text = $"{instance.Name} · {instance.MinecraftVersionId} · {instance.Loader}" +
            (isFabric ? string.Empty : " · Local JAR installation remains limited to Fabric; Modrinth follows the instance loader.");
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

        UpdateModrinthAvailability();
    }

    private void OnInstanceSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is not null && SelectedInstance is not null)
            _viewModel.SelectedInstance = SelectedInstance;
        RefreshMods();
        ResetModrinthResults();
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

    private async void OnToggleClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
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
            _busy = true;
            RefreshMods();
            await _mods.SetEnabledAsync(instance.Id, mod.FileName, !mod.IsEnabled);
            OperationStatus.Text = $"{(mod.IsEnabled ? "Disabled" : "Enabled")} '{mod.DisplayName}'.";
        }
        catch (Exception ex)
        {
            OperationStatus.Text = $"Could not change mod state: {ex.Message}";
        }
        finally
        {
            _busy = false;
            RefreshMods();
        }
    }

    private async void OnRemoveClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
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
            _busy = true;
            RefreshMods();
            await _mods.RemoveAsync(instance.Id, mod.FileName);
            OperationStatus.Text = $"Removed '{mod.DisplayName}' from '{instance.Name}'.";
        }
        catch (Exception ex)
        {
            OperationStatus.Text = $"Could not remove mod: {ex.Message}";
        }
        finally
        {
            _busy = false;
            RefreshMods();
        }
    }

    private async void OnModrinthSearchClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await SearchModrinthAsync();
    }

    private async Task SearchModrinthAsync()
    {
        if (_busy || SelectedInstance is not { } instance)
            return;
        if (!SupportsModrinth(instance.Loader))
        {
            OperationStatus.Text = $"Modrinth browsing is not supported for the '{instance.Loader}' loader.";
            UpdateModrinthAvailability();
            return;
        }

        var query = ModrinthSearchBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            OperationStatus.Text = "Enter a Modrinth search query.";
            return;
        }

        try
        {
            _busy = true;
            UpdateModrinthAvailability();
            ModrinthSearchButton.Content = "Searching…";
            SelectedModrinthDetail.Text = $"Searching Modrinth for {instance.MinecraftVersionId} · {instance.Loader}…";

            var projects = await _modrinth.SearchAsync(
                query,
                instance.MinecraftVersionId,
                instance.Loader,
                limit: 30);
            var installedMods = _mods.List(instance.Id);
            var matches = await _modrinth.MatchInstalledAsync(
                _mods.GetModsDirectory(instance.Id),
                installedMods);

            var items = projects
                .Select(project => new ModrinthBrowserItem(
                    project,
                    matches.TryGetValue(project.ProjectId, out var installed) ? installed : null))
                .ToArray();

            ModrinthResultsList.ItemsSource = items;
            ModrinthResultCount.Text = $"{items.Length} result{(items.Length == 1 ? string.Empty : "s")}";
            SelectedModrinthDetail.Text = items.Length == 0
                ? "No compatible Modrinth projects matched this search."
                : "Select a project to check its latest compatible version.";
            OperationStatus.Text = $"Modrinth returned {items.Length} compatible project{(items.Length == 1 ? string.Empty : "s")}.";
        }
        catch (Exception ex)
        {
            ModrinthResultsList.ItemsSource = Array.Empty<ModrinthBrowserItem>();
            ModrinthResultCount.Text = "0 results";
            SelectedModrinthDetail.Text = "Modrinth search failed.";
            OperationStatus.Text = $"Modrinth search failed: {ex.Message}";
        }
        finally
        {
            _busy = false;
            ModrinthSearchButton.Content = "Search";
            RefreshMods();
            UpdateModrinthSelectionButtons();
        }
    }

    private async void OnModrinthSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var generation = ++_modrinthSelectionGeneration;
        _selectedModrinthVersion = null;
        UpdateModrinthSelectionButtons();

        if (SelectedInstance is not { } instance || SelectedModrinthItem is not { } item)
        {
            SelectedModrinthDetail.Text = "Select a project to check its latest compatible version.";
            return;
        }

        OpenProjectButton.IsEnabled = true;
        SelectedModrinthDetail.Text = $"Checking latest compatible {item.Project.Title} version…";
        try
        {
            var version = await _modrinth.GetLatestCompatibleVersionAsync(
                item.Project.ProjectId,
                instance.MinecraftVersionId,
                instance.Loader);
            if (generation != _modrinthSelectionGeneration)
                return;

            _selectedModrinthVersion = version;
            if (version is null)
            {
                SelectedModrinthDetail.Text = "Modrinth has no installable JAR matching this Minecraft version and loader.";
            }
            else if (item.Installed is null)
            {
                SelectedModrinthDetail.Text = $"Latest compatible version: {version.VersionNumber}.";
            }
            else if (item.Installed.IsCurrent(version))
            {
                SelectedModrinthDetail.Text = $"Installed version {item.Installed.VersionNumber} is current.";
            }
            else
            {
                SelectedModrinthDetail.Text =
                    $"Update available: {item.Installed.VersionNumber} → {version.VersionNumber}.";
            }
        }
        catch (Exception ex)
        {
            if (generation != _modrinthSelectionGeneration)
                return;
            SelectedModrinthDetail.Text = $"Could not load compatible version: {ex.Message}";
        }

        UpdateModrinthSelectionButtons();
    }

    private async void OnModrinthInstallClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_busy
            || SelectedInstance is not { } instance
            || SelectedModrinthItem is not { } item
            || _selectedModrinthVersion is not { } version)
            return;
        if (_viewModel?.IsGameRunning == true)
        {
            OperationStatus.Text = "Stop Minecraft before changing the instance mod set.";
            RefreshMods();
            return;
        }

        try
        {
            _busy = true;
            RefreshMods();
            UpdateModrinthSelectionButtons();
            OperationStatus.Text = $"Downloading {item.Project.Title} {version.VersionNumber} from Modrinth…";

            var result = await _modrinth.InstallAsync(
                instance.Id,
                item.Project,
                version,
                item.Installed,
                _mods);

            var match = new ModProviderInstalledMatch(
                _modrinth.ProviderId,
                result.Project.ProjectId,
                result.Version.VersionId,
                result.Version.VersionNumber,
                result.InstalledMod.FileName,
                result.InstalledMod.IsEnabled);

            ReplaceBrowserItem(item, new ModrinthBrowserItem(item.Project, match));
            OperationStatus.Text =
                $"Installed {result.Project.Title} {result.Version.VersionNumber} into '{instance.Name}'.";
            SelectedModrinthDetail.Text = $"Installed version {result.Version.VersionNumber} is current.";
        }
        catch (Exception ex)
        {
            OperationStatus.Text = $"Modrinth installation failed: {ex.Message}";
        }
        finally
        {
            _busy = false;
            RefreshMods();
            UpdateModrinthSelectionButtons();
        }
    }

    private void OnOpenProjectClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (SelectedModrinthItem is not { } item)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(item.Project.ProjectUrl)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            OperationStatus.Text = $"Could not open the Modrinth project page: {ex.Message}";
        }
    }

    private void ReplaceBrowserItem(ModrinthBrowserItem oldItem, ModrinthBrowserItem newItem)
    {
        var items = (ModrinthResultsList.ItemsSource as IEnumerable<ModrinthBrowserItem>)?.ToArray() ?? [];
        var index = Array.IndexOf(items, oldItem);
        if (index < 0)
            return;
        items[index] = newItem;
        ModrinthResultsList.ItemsSource = items;
        ModrinthResultsList.SelectedItem = newItem;
    }

    private void ResetModrinthResults()
    {
        _modrinthSelectionGeneration++;
        _selectedModrinthVersion = null;
        ModrinthResultsList.ItemsSource = Array.Empty<ModrinthBrowserItem>();
        ModrinthResultCount.Text = "0 results";
        SelectedModrinthDetail.Text = SelectedInstance is null
            ? "Choose an instance before searching Modrinth."
            : "Search Modrinth to find mods compatible with this instance.";
        OpenProjectButton.IsEnabled = false;
        ModrinthInstallButton.IsEnabled = false;
        ModrinthInstallButton.Content = "Install selected";
        UpdateModrinthAvailability();
    }

    private void UpdateModrinthAvailability()
    {
        var instance = SelectedInstance;
        var supported = instance is not null && SupportsModrinth(instance.Loader);
        ModrinthSearchButton.IsEnabled = !_busy && supported;
        if (instance is not null && !supported && !_busy)
            SelectedModrinthDetail.Text = $"Modrinth browsing is unavailable for loader '{instance.Loader}'.";
        UpdateModrinthSelectionButtons();
    }

    private void UpdateModrinthSelectionButtons()
    {
        var item = SelectedModrinthItem;
        OpenProjectButton.IsEnabled = item is not null;
        var canInstall = !_busy
                         && item is not null
                         && _selectedModrinthVersion is not null
                         && _viewModel?.IsGameRunning != true;
        ModrinthInstallButton.IsEnabled = canInstall;
        ModrinthInstallButton.Content = item?.Installed is null
            ? "Install selected"
            : _selectedModrinthVersion is not null && item.Installed.IsCurrent(_selectedModrinthVersion)
                ? "Reinstall selected"
                : "Update selected";
    }

    private static bool SupportsModrinth(string loader)
        => loader.Equals("fabric", StringComparison.OrdinalIgnoreCase)
           || loader.Equals("forge", StringComparison.OrdinalIgnoreCase)
           || loader.Equals("neoforge", StringComparison.OrdinalIgnoreCase)
           || loader.Equals("quilt", StringComparison.OrdinalIgnoreCase);

    private static string BuildModrinthUserAgent()
    {
        var version = typeof(ModManagerWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "dev";
        var safeVersion = version.Split('+', 2)[0];
        return $"yannickfan67-ai-UN_Nexo/{safeVersion} (github.com/yannickfan67-ai/UN_Nexo)";
    }
}
