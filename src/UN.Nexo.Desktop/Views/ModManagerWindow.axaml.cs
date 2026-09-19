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
    private readonly HttpClient _curseForgeHttpClient;
    private readonly IModDependencyProvider _modrinth;
    private readonly CurseForgeModProvider _curseForge;
    private ModProviderVersion? _selectedModrinthVersion;
    private ModDependencyPlan? _selectedModrinthPlan;
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
        _modrinth = new ModrinthModProvider(_modrinthHttpClient, BuildProviderUserAgent());

        _curseForgeHttpClient = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false
        })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _curseForge = new CurseForgeModProvider(
            _curseForgeHttpClient,
            new EnvironmentCurseForgeApiKeyProvider(),
            BuildProviderUserAgent());

        ProviderBox.ItemsSource = new[] { "Modrinth", "CurseForge" };
        ProviderBox.SelectedIndex = 0;

        Opened += (_, _) => RefreshInstances();
        Closed += (_, _) =>
        {
            _modrinthHttpClient.Dispose();
            _curseForgeHttpClient.Dispose();
        };
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
            (isFabric ? string.Empty : " · Local JAR installation remains limited to Fabric; provider browsing follows the instance loader.");
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

    private IModDependencyProvider ActiveProvider
        => ProviderBox.SelectedIndex == 1
            ? _curseForge
            : _modrinth;

    private bool ActiveProviderConfigured
        => ActiveProvider is not CurseForgeModProvider curseForge
           || curseForge.IsConfigured;

    private void OnProviderSelectionChanged(
        object? sender,
        SelectionChangedEventArgs e)
    {
        ResetModrinthResults();
        UpdateModrinthAvailability();
    }

    private async void OnModrinthSearchClicked(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        await SearchProviderAsync();
    }

    private async void OnModrinthRecommendClicked(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        await RecommendProviderAsync();
    }

    private async Task RecommendProviderAsync()
    {
        if (_busy || SelectedInstance is not { } instance)
            return;

        var provider = ActiveProvider;
        if (provider is not IModRecommendationProvider recommendations)
        {
            OperationStatus.Text =
                $"{provider.DisplayName} does not expose recommendations.";
            return;
        }

        if (!SupportsProvider(instance.Loader))
        {
            OperationStatus.Text =
                $"{provider.DisplayName} recommendations are not supported for the '{instance.Loader}' loader.";
            UpdateModrinthAvailability();
            return;
        }

        if (!ActiveProviderConfigured)
        {
            OperationStatus.Text =
                $"CurseForge is not configured. Set {EnvironmentCurseForgeApiKeyProvider.EnvironmentVariableName} for direct API access, or {CurseForgeModProvider.ApiBaseEnvironmentVariableName} for a server-side proxy.";
            UpdateModrinthAvailability();
            return;
        }

        try
        {
            _busy = true;
            UpdateModrinthAvailability();
            ModrinthRecommendButton.Content = "Loading…";
            SelectedModrinthDetail.Text =
                $"Loading compatible {provider.DisplayName} recommendations for {instance.MinecraftVersionId} · {instance.Loader}…";

            var suggested = await recommendations.RecommendAsync(
                instance.MinecraftVersionId,
                instance.Loader,
                limit: provider.ProviderId.Equals(
                    "curseforge",
                    StringComparison.OrdinalIgnoreCase)
                    ? 30
                    : 40);

            var installedMods = _mods.List(instance.Id);
            var matches = await provider.MatchInstalledAsync(
                _mods.GetModsDirectory(instance.Id),
                installedMods);

            var items = suggested
                .Where(recommendation =>
                    !matches.ContainsKey(
                        recommendation.Project.ProjectId))
                .Take(20)
                .Select(recommendation =>
                    new ModrinthBrowserItem(
                        recommendation.Project,
                        installed: null,
                        recommendation.Signal))
                .ToArray();

            ModrinthResultsList.ItemsSource = items;
            ModrinthResultCount.Text =
                $"{items.Length} recommendation{(items.Length == 1 ? string.Empty : "s")}";
            SelectedModrinthDetail.Text = items.Length == 0
                ? $"No new compatible {provider.DisplayName} recommendations were found after excluding installed projects."
                : "Recommendations are suggestions only. Select one to inspect its exact compatible version and dependencies before choosing Install.";
            OperationStatus.Text =
                $"Loaded {items.Length} {provider.DisplayName} recommendation{(items.Length == 1 ? string.Empty : "s")} · installed projects excluded · nothing installed automatically.";
        }
        catch (Exception ex)
        {
            ModrinthResultsList.ItemsSource =
                Array.Empty<ModrinthBrowserItem>();
            ModrinthResultCount.Text = "0 recommendations";
            SelectedModrinthDetail.Text =
                $"{provider.DisplayName} recommendations failed.";
            OperationStatus.Text =
                $"{provider.DisplayName} recommendations failed: {ex.Message}";
        }
        finally
        {
            _busy = false;
            ModrinthRecommendButton.Content = "Recommend";
            RefreshMods();
            UpdateModrinthSelectionButtons();
        }
    }

    private async Task SearchProviderAsync()
    {
        if (_busy || SelectedInstance is not { } instance)
            return;

        var provider = ActiveProvider;
        if (!SupportsProvider(instance.Loader))
        {
            OperationStatus.Text =
                $"{provider.DisplayName} browsing is not supported for the '{instance.Loader}' loader.";
            UpdateModrinthAvailability();
            return;
        }

        if (!ActiveProviderConfigured)
        {
            OperationStatus.Text =
                $"CurseForge is not configured. Set {EnvironmentCurseForgeApiKeyProvider.EnvironmentVariableName} for direct API access, or {CurseForgeModProvider.ApiBaseEnvironmentVariableName} for a server-side proxy; Nexo never embeds the CurseForge key.";
            UpdateModrinthAvailability();
            return;
        }

        var query = ModrinthSearchBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            OperationStatus.Text = $"Enter a {provider.DisplayName} search query.";
            return;
        }

        try
        {
            _busy = true;
            UpdateModrinthAvailability();
            ModrinthSearchButton.Content = "Searching…";
            SelectedModrinthDetail.Text =
                $"Searching {provider.DisplayName} for {instance.MinecraftVersionId} · {instance.Loader}…";

            var projects = await provider.SearchAsync(
                query,
                instance.MinecraftVersionId,
                instance.Loader,
                limit: provider.ProviderId.Equals("curseforge", StringComparison.OrdinalIgnoreCase)
                    ? 20
                    : 30);
            var installedMods = _mods.List(instance.Id);
            var matches = await provider.MatchInstalledAsync(
                _mods.GetModsDirectory(instance.Id),
                installedMods);

            var items = projects
                .Select(project => new ModrinthBrowserItem(
                    project,
                    matches.TryGetValue(project.ProjectId, out var installed)
                        ? installed
                        : null))
                .ToArray();

            ModrinthResultsList.ItemsSource = items;
            ModrinthResultCount.Text =
                $"{items.Length} result{(items.Length == 1 ? string.Empty : "s")}";
            SelectedModrinthDetail.Text = items.Length == 0
                ? $"No compatible {provider.DisplayName} projects matched this search."
                : "Select a project to check its latest compatible version.";
            OperationStatus.Text =
                $"{provider.DisplayName} returned {items.Length} compatible project{(items.Length == 1 ? string.Empty : "s")}.";
        }
        catch (Exception ex)
        {
            ModrinthResultsList.ItemsSource = Array.Empty<ModrinthBrowserItem>();
            ModrinthResultCount.Text = "0 results";
            SelectedModrinthDetail.Text = $"{provider.DisplayName} search failed.";
            OperationStatus.Text =
                $"{provider.DisplayName} search failed: {ex.Message}";
        }
        finally
        {
            _busy = false;
            ModrinthSearchButton.Content = "Search";
            RefreshMods();
            UpdateModrinthSelectionButtons();
        }
    }

    private async void OnModrinthSelectionChanged(
        object? sender,
        SelectionChangedEventArgs e)
    {
        var generation = ++_modrinthSelectionGeneration;
        _selectedModrinthVersion = null;
        _selectedModrinthPlan = null;
        UpdateModrinthSelectionButtons();

        var provider = ActiveProvider;
        if (SelectedInstance is not { } instance
            || SelectedModrinthItem is not { } item)
        {
            SelectedModrinthDetail.Text =
                "Select a project to check its latest compatible version.";
            return;
        }

        if (!string.Equals(
                item.Project.ProviderId,
                provider.ProviderId,
                StringComparison.OrdinalIgnoreCase))
        {
            ResetModrinthResults();
            return;
        }

        OpenProjectButton.IsEnabled = true;
        SelectedModrinthDetail.Text =
            $"Checking latest compatible {item.Project.Title} version on {provider.DisplayName}…";
        try
        {
            var version = await provider.GetLatestCompatibleVersionAsync(
                item.Project.ProjectId,
                instance.MinecraftVersionId,
                instance.Loader);
            if (generation != _modrinthSelectionGeneration)
                return;

            _selectedModrinthVersion = version;
            if (version is null)
            {
                SelectedModrinthDetail.Text =
                    $"{provider.DisplayName} has no installable JAR matching this Minecraft version and loader.";
            }
            else
            {
                SelectedModrinthDetail.Text =
                    $"Resolving required dependencies for {item.Project.Title} {version.VersionNumber}…";
                var plan = await new ModDependencyPlanner(provider).BuildAsync(
                    item.Project,
                    version,
                    instance.MinecraftVersionId,
                    instance.Loader);
                if (generation != _modrinthSelectionGeneration)
                    return;

                _selectedModrinthPlan = plan;
                var dependencies = plan.InstallOrder
                    .Where(entry => !entry.IsRoot)
                    .Select(entry => entry.Project.Title)
                    .ToArray();
                var dependencyText = dependencies.Length == 0
                    ? "No required dependencies."
                    : $"Required dependencies ({dependencies.Length}): {string.Join(", ", dependencies)}.";
                var optionalText = plan.OptionalDependencies.Count == 0
                    ? string.Empty
                    : $" Optional dependencies advertised: {plan.OptionalDependencies.Count}; they are not auto-installed.";

                var versionText = item.Installed is null
                    ? $"Latest compatible version: {version.VersionNumber}."
                    : item.Installed.IsCurrent(version)
                        ? $"Installed version {item.Installed.VersionNumber} is current."
                        : $"Update available: {item.Installed.VersionNumber} → {version.VersionNumber}.";

                SelectedModrinthDetail.Text =
                    $"{versionText} {dependencyText}{optionalText} The complete required plan is downloaded first and published together.";
            }
        }
        catch (Exception ex)
        {
            if (generation != _modrinthSelectionGeneration)
                return;
            SelectedModrinthDetail.Text =
                $"Could not load compatible version: {ex.Message}";
        }

        UpdateModrinthSelectionButtons();
    }

    private async void OnModrinthInstallClicked(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_busy
            || SelectedInstance is not { } instance
            || SelectedModrinthItem is not { } item
            || _selectedModrinthVersion is not { } version)
            return;
        if (_viewModel?.IsGameRunning == true)
        {
            OperationStatus.Text =
                "Stop Minecraft before changing the instance mod set.";
            RefreshMods();
            return;
        }

        var provider = ActiveProvider;
        if (!string.Equals(
                item.Project.ProviderId,
                provider.ProviderId,
                StringComparison.OrdinalIgnoreCase))
        {
            OperationStatus.Text =
                "The selected provider changed. Search again before installing.";
            ResetModrinthResults();
            return;
        }

        try
        {
            _busy = true;
            RefreshMods();
            UpdateModrinthSelectionButtons();
            var plan = _selectedModrinthPlan
                ?? throw new InvalidOperationException(
                    $"Resolve the {provider.DisplayName} dependency plan before installing.");
            OperationStatus.Text =
                $"Downloading {plan.InstallOrder.Count} planned mod file{(plan.InstallOrder.Count == 1 ? string.Empty : "s")} from {provider.DisplayName}…";

            var installedBefore = _mods.List(instance.Id);
            var matchesBefore = await provider.MatchInstalledAsync(
                _mods.GetModsDirectory(instance.Id),
                installedBefore);
            var result = await new ModDependencyInstaller(provider, _mods).InstallAsync(
                instance.Id,
                plan,
                matchesBefore);

            var installedAfter = _mods.List(instance.Id);
            var matchesAfter = await provider.MatchInstalledAsync(
                _mods.GetModsDirectory(instance.Id),
                installedAfter);
            var rootMatch = matchesAfter.TryGetValue(
                item.Project.ProjectId,
                out var currentRoot)
                ? currentRoot
                : item.Installed;

            ReplaceBrowserItem(
                item,
                new ModrinthBrowserItem(
                    item.Project,
                    rootMatch,
                    item.RecommendationSignal));
            OperationStatus.Text = result.Installed.Count == 0
                ? $"'{item.Project.Title}' and all required dependencies are already current."
                : $"Published {result.Installed.Count} mod file{(result.Installed.Count == 1 ? string.Empty : "s")} atomically into '{instance.Name}'.";
            SelectedModrinthDetail.Text =
                $"Install plan complete · {plan.RequiredDependencyCount} required dependenc{(plan.RequiredDependencyCount == 1 ? "y" : "ies")} · root {version.VersionNumber}.";
        }
        catch (Exception ex)
        {
            OperationStatus.Text =
                $"{provider.DisplayName} installation failed: {ex.Message}";
        }
        finally
        {
            _busy = false;
            RefreshMods();
            UpdateModrinthSelectionButtons();
        }
    }

    private void OnOpenProjectClicked(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
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
            OperationStatus.Text =
                $"Could not open the project page: {ex.Message}";
        }
    }

    private void ReplaceBrowserItem(
        ModrinthBrowserItem oldItem,
        ModrinthBrowserItem newItem)
    {
        var items =
            (ModrinthResultsList.ItemsSource as IEnumerable<ModrinthBrowserItem>)
            ?.ToArray()
            ?? [];
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
        _selectedModrinthPlan = null;
        ModrinthResultsList.ItemsSource =
            Array.Empty<ModrinthBrowserItem>();
        ModrinthResultCount.Text = "0 results";

        var provider = ActiveProvider;
        SelectedModrinthDetail.Text = SelectedInstance is null
            ? $"Choose an instance before searching {provider.DisplayName}."
            : !ActiveProviderConfigured
                ? $"CurseForge needs {EnvironmentCurseForgeApiKeyProvider.EnvironmentVariableName} for direct API access, or {CurseForgeModProvider.ApiBaseEnvironmentVariableName} for a server-side proxy. Nexo does not embed or persist the key."
                : $"Search {provider.DisplayName}, or use Recommend for compatible popular projects. Recommendations never install automatically.";

        OpenProjectButton.IsEnabled = false;
        ModrinthInstallButton.IsEnabled = false;
        ModrinthInstallButton.Content = "Install selected";
        UpdateModrinthAvailability();
    }

    private void UpdateModrinthAvailability()
    {
        var provider = ActiveProvider;
        var instance = SelectedInstance;
        var supported =
            instance is not null
            && SupportsProvider(instance.Loader);
        var configured = ActiveProviderConfigured;

        ModrinthSearchButton.IsEnabled =
            !_busy && supported && configured;
        ModrinthRecommendButton.IsEnabled =
            !_busy
            && supported
            && configured
            && provider is IModRecommendationProvider;

        ProviderPolicyLabel.Text = provider is CurseForgeModProvider curseForge
            ? curseForge.UsesServerSideCredentialProxy
                ? $"CurseForge proxy · {CurseForgeModProvider.ApiBaseEnvironmentVariableName} · API key stays server-side"
                : configured
                    ? $"CurseForge API · key from {EnvironmentCurseForgeApiKeyProvider.EnvironmentVariableName} · not persisted"
                    : $"CurseForge disabled · set {EnvironmentCurseForgeApiKeyProvider.EnvironmentVariableName} or {CurseForgeModProvider.ApiBaseEnvironmentVariableName}"
            : "Public Modrinth API · no private API key";

        if (instance is not null && !supported && !_busy)
        {
            SelectedModrinthDetail.Text =
                $"{provider.DisplayName} browsing is unavailable for loader '{instance.Loader}'.";
        }
        else if (!configured && !_busy)
        {
            SelectedModrinthDetail.Text =
                $"CurseForge is disabled until {EnvironmentCurseForgeApiKeyProvider.EnvironmentVariableName} or {CurseForgeModProvider.ApiBaseEnvironmentVariableName} is set before launch.";
        }

        UpdateModrinthSelectionButtons();
    }

    private void UpdateModrinthSelectionButtons()
    {
        var item = SelectedModrinthItem;
        OpenProjectButton.IsEnabled = item is not null;
        var provider = ActiveProvider;
        var matchesProvider = item is not null
            && string.Equals(
                item.Project.ProviderId,
                provider.ProviderId,
                StringComparison.OrdinalIgnoreCase);

        var canInstall = !_busy
                         && matchesProvider
                         && ActiveProviderConfigured
                         && _selectedModrinthVersion is not null
                         && _selectedModrinthPlan is not null
                         && _viewModel?.IsGameRunning != true;
        ModrinthInstallButton.IsEnabled = canInstall;
        ModrinthInstallButton.Content = item?.Installed is null
            ? "Install selected"
            : _selectedModrinthVersion is not null
              && item.Installed.IsCurrent(_selectedModrinthVersion)
                ? "Reinstall selected"
                : "Update selected";
    }

    private static bool SupportsProvider(string loader)
        => loader.Equals("fabric", StringComparison.OrdinalIgnoreCase)
           || loader.Equals("forge", StringComparison.OrdinalIgnoreCase)
           || loader.Equals("neoforge", StringComparison.OrdinalIgnoreCase)
           || loader.Equals("quilt", StringComparison.OrdinalIgnoreCase);

    private static string BuildProviderUserAgent()
    {
        var version = typeof(ModManagerWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? "dev";
        var safeVersion = version.Split('+', 2)[0];
        return
            $"yannickfan67-ai-UN_Nexo/{safeVersion} (github.com/yannickfan67-ai/UN_Nexo)";
    }
}
