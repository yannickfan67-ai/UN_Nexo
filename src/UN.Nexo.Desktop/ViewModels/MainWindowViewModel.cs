using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Desktop.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly JavaDiscoveryService _javaDiscovery;
    private readonly NexoPathService _paths;
    private readonly MinecraftVersionManifestService _manifest;
    private readonly InstanceStoreService _instances;
    private readonly MinecraftVanillaInstallService _installer;
    private readonly AccountStoreService _accounts;
    private readonly LauncherSettingsService _settings;
    private readonly DownloadSourceService _downloadSources;

    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isInstallBusy;
    [ObservableProperty] private bool isHomeVisible = true;
    [ObservableProperty] private bool isInstancesVisible;
    [ObservableProperty] private bool isAccountsVisible;
    [ObservableProperty] private bool isSettingsVisible;
    [ObservableProperty] private bool hasSelectedInstance;
    [ObservableProperty] private bool hasSelectedVersion;
    [ObservableProperty] private bool hasSelectedAccount;
    [ObservableProperty] private string launcherStatus = "Ready";
    [ObservableProperty] private string minecraftDirectory = "Detecting…";
    [ObservableProperty] private string instanceRoot = "Detecting…";
    [ObservableProperty] private string javaSummary = "Not scanned";
    [ObservableProperty] private string javaDetail = "Run environment scan to detect Java.";
    [ObservableProperty] private string latestRelease = "Unknown";
    [ObservableProperty] private string latestSnapshot = "Unknown";
    [ObservableProperty] private string instanceSummary = "0 instances";
    [ObservableProperty] private string catalogStatus = "Loading version catalog…";
    [ObservableProperty] private string newInstanceName = "New Minecraft";
    [ObservableProperty] private MinecraftVersionInfo? selectedVersion;
    [ObservableProperty] private GameInstance? selectedInstance;
    [ObservableProperty] private LauncherAccount? selectedAccount;
    [ObservableProperty] private double installProgressValue;
    [ObservableProperty] private string installProgressText = "Not installed";
    [ObservableProperty] private string offlineUserName = string.Empty;
    [ObservableProperty] private string accountSummary = "No account selected";
    [ObservableProperty] private string selectedDownloadSource = "Official";
    [ObservableProperty] private string downloadSourceStatus = "Official Mojang/Minecraft services";
    [ObservableProperty] private string microsoftAuthStatus = "Microsoft sign-in needs an approved UN_Nexo application registration before Minecraft Services will accept the client ID.";

    public ObservableCollection<JavaInstallation> JavaInstallations { get; } = [];
    public ObservableCollection<MinecraftVersionInfo> AvailableVersions { get; } = [];
    public ObservableCollection<GameInstance> Instances { get; } = [];
    public ObservableCollection<LauncherAccount> Accounts { get; } = [];
    public ObservableCollection<string> DownloadSources { get; } = ["Official", "BMCLAPI"];

    public MainWindowViewModel(
        JavaDiscoveryService javaDiscovery,
        NexoPathService paths,
        MinecraftVersionManifestService manifest,
        InstanceStoreService instances,
        MinecraftVanillaInstallService installer,
        AccountStoreService accounts,
        LauncherSettingsService settings,
        DownloadSourceService downloadSources)
    {
        _javaDiscovery = javaDiscovery;
        _paths = paths;
        _manifest = manifest;
        _instances = instances;
        _installer = installer;
        _accounts = accounts;
        _settings = settings;
        _downloadSources = downloadSources;
    }

    public async Task InitializeAsync()
    {
        var settings = await _settings.LoadAsync();
        _downloadSources.SetSource(settings.DownloadSource);
        SelectedDownloadSource = _downloadSources.SourceId == "bmclapi" ? "BMCLAPI" : "Official";
        UpdateDownloadSourceStatus();
        await ReloadAccountsAsync();
        await RefreshEnvironmentAsync();
    }

    partial void OnSelectedVersionChanged(MinecraftVersionInfo? value)
    {
        HasSelectedVersion = value is not null;
        if (value is not null
            && (NewInstanceName == "New Minecraft" || NewInstanceName.StartsWith("Minecraft ", StringComparison.Ordinal)))
            NewInstanceName = $"Minecraft {value.Id}";
    }

    partial void OnSelectedInstanceChanged(GameInstance? value)
    {
        HasSelectedInstance = value is not null;
        if (value is null)
        {
            InstallProgressValue = 0;
            InstallProgressText = "Not installed";
            return;
        }

        var prepared = File.Exists(Path.Combine(_paths.GetInstanceDirectory(value.Id), "install-state.json"));
        InstallProgressValue = prepared ? 100 : 0;
        InstallProgressText = prepared ? "Vanilla files prepared" : "Not installed";
    }

    partial void OnSelectedAccountChanged(LauncherAccount? value)
    {
        HasSelectedAccount = value is not null;
        AccountSummary = value is null
            ? "No account selected"
            : value.IsOffline
                ? $"{value.DisplayName} · Offline profile"
                : $"{value.DisplayName} · Microsoft";
    }

    [RelayCommand]
    private void ShowHome() => ShowPage("home");

    [RelayCommand]
    private void ShowInstances() => ShowPage("instances");

    [RelayCommand]
    private void ShowAccounts() => ShowPage("accounts");

    [RelayCommand]
    private void ShowSettings() => ShowPage("settings");

    private void ShowPage(string page)
    {
        IsHomeVisible = page == "home";
        IsInstancesVisible = page == "instances";
        IsAccountsVisible = page == "accounts";
        IsSettingsVisible = page == "settings";
    }

    [RelayCommand]
    private async Task RefreshEnvironmentAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        LauncherStatus = $"Scanning environment · {_downloadSources.DisplayName} source…";
        MinecraftDirectory = _paths.GetMinecraftDirectory();
        InstanceRoot = _paths.GetInstancesRoot();
        _paths.EnsureDirectories();

        var previousInstanceId = SelectedInstance?.Id;
        var previousVersionId = SelectedVersion?.Id;

        try
        {
            var javaTask = _javaDiscovery.DiscoverAsync();
            var catalogTask = TryGetCatalogAsync();
            var instancesTask = _instances.GetAllAsync();

            var java = await javaTask;
            var catalog = await catalogTask;
            var instances = await instancesTask;

            JavaInstallations.Clear();
            foreach (var installation in java)
                JavaInstallations.Add(installation);

            Instances.Clear();
            foreach (var instance in instances)
                Instances.Add(instance);

            AvailableVersions.Clear();
            if (catalog is not null)
            {
                var releases = catalog.Versions.Where(v => v.Type.Equals("release", StringComparison.OrdinalIgnoreCase)).ToArray();
                var recentSnapshots = catalog.Versions
                    .Where(v => v.Type.Equals("snapshot", StringComparison.OrdinalIgnoreCase))
                    .Take(40)
                    .ToArray();

                foreach (var version in releases.Concat(recentSnapshots))
                    AvailableVersions.Add(version);

                LatestRelease = catalog.Latest.LatestRelease;
                LatestSnapshot = catalog.Latest.LatestSnapshot;
                CatalogStatus = $"{releases.Length} releases · {recentSnapshots.Length} recent snapshots · {_downloadSources.DisplayName}";

                SelectedVersion = AvailableVersions.FirstOrDefault(v => v.Id == previousVersionId)
                    ?? AvailableVersions.FirstOrDefault(v => v.Id == catalog.Latest.LatestRelease)
                    ?? AvailableVersions.FirstOrDefault();
            }
            else
            {
                LatestRelease = "Offline";
                LatestSnapshot = "Offline";
                CatalogStatus = $"Version catalog unavailable · {_downloadSources.DisplayName} and fallback failed";
                SelectedVersion = null;
            }

            SelectedInstance = Instances.FirstOrDefault(v => v.Id == previousInstanceId)
                ?? Instances.FirstOrDefault();

            JavaSummary = java.Count == 0 ? "No Java found" : $"{java.Count} Java installation{(java.Count == 1 ? string.Empty : "s")}";
            JavaDetail = java.Count == 0
                ? "No Java runtime detected. Nexo will support per-instance runtime selection."
                : $"Preferred: Java {java[0].Version} · {(java[0].Is64Bit ? "64-bit" : "architecture unknown")}\n{java[0].JavaPath}";

            InstanceSummary = $"{instances.Count} instance{(instances.Count == 1 ? string.Empty : "s")}";
            LauncherStatus = catalog is null ? "Ready · version service offline" : $"Ready · {_downloadSources.DisplayName}";
        }
        catch (Exception ex)
        {
            CatalogStatus = "Version catalog unavailable";
            LauncherStatus = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CreateInstanceAsync()
    {
        if (IsBusy || SelectedVersion is null)
            return;

        var name = string.IsNullOrWhiteSpace(NewInstanceName) ? SelectedVersion.Id : NewInstanceName.Trim();
        IsBusy = true;
        LauncherStatus = $"Creating {name}…";
        try
        {
            var instance = await _instances.CreateAsync(name, SelectedVersion.Id);
            Instances.Add(instance);
            SelectedInstance = instance;
            InstanceSummary = $"{Instances.Count} instance{(Instances.Count == 1 ? string.Empty : "s")}";
            LauncherStatus = $"Created {instance.Name}";
        }
        catch (Exception ex)
        {
            LauncherStatus = $"Create failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task PrepareSelectedInstanceAsync()
    {
        if (IsInstallBusy || SelectedInstance is null)
            return;

        var targetInstance = SelectedInstance;
        var targetSource = _downloadSources.DisplayName;
        var version = AvailableVersions.FirstOrDefault(v => v.Id == targetInstance.VersionId);
        if (version is null)
        {
            LauncherStatus = "Version metadata is unavailable. Refresh the catalog first.";
            return;
        }

        IsInstallBusy = true;
        InstallProgressValue = 0;
        InstallProgressText = $"Starting via {targetSource}…";
        LauncherStatus = $"Preparing {targetInstance.Name}…";

        var progress = new Progress<InstallProgress>(value =>
        {
            if (!IsSelectedInstance(targetInstance))
                return;

            InstallProgressValue = value.Percent;
            InstallProgressText = value.Total > 0
                ? $"{value.Stage} · {value.Completed}/{value.Total}{(string.IsNullOrWhiteSpace(value.CurrentItem) ? string.Empty : $" · {value.CurrentItem}")}" 
                : value.Stage;
        });

        try
        {
            await _installer.InstallAsync(targetInstance, version, progress);
            if (IsSelectedInstance(targetInstance))
            {
                InstallProgressValue = 100;
                InstallProgressText = $"Vanilla files prepared · {targetSource}";
            }
            LauncherStatus = $"{targetInstance.Name} is prepared";
        }
        catch (Exception ex)
        {
            if (IsSelectedInstance(targetInstance))
                InstallProgressText = "Install failed";
            LauncherStatus = $"Install failed for {targetInstance.Name}: {ex.Message}";
        }
        finally
        {
            IsInstallBusy = false;
            if (!IsSelectedInstance(targetInstance) && SelectedInstance is not null)
                RefreshSelectedInstanceInstallState();
        }
    }

    [RelayCommand]
    private async Task CreateOfflineAccountAsync()
    {
        try
        {
            var account = await _accounts.CreateOfflineAsync(OfflineUserName);
            var existing = Accounts.FirstOrDefault(x => x.Id == account.Id);
            if (existing is null)
                Accounts.Add(account);
            SelectedAccount = existing ?? account;
            OfflineUserName = string.Empty;
            LauncherStatus = $"Offline profile {account.DisplayName} is ready";
        }
        catch (Exception ex)
        {
            LauncherStatus = $"Offline profile: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ExplainMicrosoftSignIn()
    {
        MicrosoftAuthStatus = "The Microsoft → Xbox Live → XSTS → Minecraft Services flow is planned, but Minecraft Services now rejects unapproved third-party application IDs. Nexo will use its own approved client ID only; it will not borrow another launcher's identity.";
        LauncherStatus = "Microsoft sign-in is waiting for UN_Nexo app registration approval";
    }

    [RelayCommand]
    private async Task SaveDownloadSourceAsync()
    {
        var id = SelectedDownloadSource.Equals("BMCLAPI", StringComparison.OrdinalIgnoreCase)
            ? "bmclapi"
            : "official";
        _downloadSources.SetSource(id);
        await _settings.SaveAsync(new LauncherSettings(id));
        UpdateDownloadSourceStatus();
        LauncherStatus = $"Download source changed to {_downloadSources.DisplayName}";
        await RefreshEnvironmentAsync();
    }

    private async Task ReloadAccountsAsync()
    {
        var previousId = SelectedAccount?.Id;
        var accounts = await _accounts.GetAllAsync();
        Accounts.Clear();
        foreach (var account in accounts)
            Accounts.Add(account);
        SelectedAccount = Accounts.FirstOrDefault(x => x.Id == previousId) ?? Accounts.FirstOrDefault();
    }

    private bool IsSelectedInstance(GameInstance instance)
        => SelectedInstance?.Id == instance.Id;

    private void RefreshSelectedInstanceInstallState()
    {
        if (SelectedInstance is null)
        {
            InstallProgressValue = 0;
            InstallProgressText = "Not installed";
            return;
        }

        var prepared = File.Exists(Path.Combine(_paths.GetInstanceDirectory(SelectedInstance.Id), "install-state.json"));
        InstallProgressValue = prepared ? 100 : 0;
        InstallProgressText = prepared ? "Vanilla files prepared" : "Not installed";
    }

    private void UpdateDownloadSourceStatus()
    {
        DownloadSourceStatus = _downloadSources.SourceId == "bmclapi"
            ? "BMCLAPI mirror first · automatic fallback to official Mojang/Minecraft URLs"
            : "Official Mojang/Minecraft services only";
    }

    private async Task<MinecraftVersionCatalog?> TryGetCatalogAsync()
    {
        try
        {
            return await _manifest.GetCatalogAsync();
        }
        catch
        {
            return null;
        }
    }
}
