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

    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isInstallBusy;
    [ObservableProperty] private bool isHomeVisible = true;
    [ObservableProperty] private bool isInstancesVisible;
    [ObservableProperty] private bool hasSelectedInstance;
    [ObservableProperty] private bool hasSelectedVersion;
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
    [ObservableProperty] private double installProgressValue;
    [ObservableProperty] private string installProgressText = "Not installed";

    public ObservableCollection<JavaInstallation> JavaInstallations { get; } = [];
    public ObservableCollection<MinecraftVersionInfo> AvailableVersions { get; } = [];
    public ObservableCollection<GameInstance> Instances { get; } = [];

    public MainWindowViewModel(
        JavaDiscoveryService javaDiscovery,
        NexoPathService paths,
        MinecraftVersionManifestService manifest,
        InstanceStoreService instances,
        MinecraftVanillaInstallService installer)
    {
        _javaDiscovery = javaDiscovery;
        _paths = paths;
        _manifest = manifest;
        _instances = instances;
        _installer = installer;
    }

    public Task InitializeAsync() => RefreshEnvironmentAsync();

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

    [RelayCommand]
    private void ShowHome()
    {
        IsHomeVisible = true;
        IsInstancesVisible = false;
    }

    [RelayCommand]
    private void ShowInstances()
    {
        IsHomeVisible = false;
        IsInstancesVisible = true;
    }

    [RelayCommand]
    private async Task RefreshEnvironmentAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        LauncherStatus = "Scanning environment…";
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
                CatalogStatus = $"{releases.Length} releases · {recentSnapshots.Length} recent snapshots";

                SelectedVersion = AvailableVersions.FirstOrDefault(v => v.Id == previousVersionId)
                    ?? AvailableVersions.FirstOrDefault(v => v.Id == catalog.Latest.LatestRelease)
                    ?? AvailableVersions.FirstOrDefault();
            }
            else
            {
                LatestRelease = "Offline";
                LatestSnapshot = "Offline";
                CatalogStatus = "Version catalog unavailable · check network and refresh";
                SelectedVersion = null;
            }

            SelectedInstance = Instances.FirstOrDefault(v => v.Id == previousInstanceId)
                ?? Instances.FirstOrDefault();

            JavaSummary = java.Count == 0 ? "No Java found" : $"{java.Count} Java installation{(java.Count == 1 ? string.Empty : "s")}";
            JavaDetail = java.Count == 0
                ? "No Java runtime detected. Nexo will support per-instance runtime selection."
                : $"Preferred: Java {java[0].Version} · {(java[0].Is64Bit ? "64-bit" : "architecture unknown")}\n{java[0].JavaPath}";

            InstanceSummary = $"{instances.Count} instance{(instances.Count == 1 ? string.Empty : "s")}";
            LauncherStatus = catalog is null ? "Ready · version service offline" : "Ready";
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

        var version = AvailableVersions.FirstOrDefault(v => v.Id == SelectedInstance.VersionId);
        if (version is null)
        {
            LauncherStatus = "Version metadata is unavailable. Refresh the catalog first.";
            return;
        }

        IsInstallBusy = true;
        InstallProgressValue = 0;
        InstallProgressText = "Starting…";
        LauncherStatus = $"Preparing {SelectedInstance.Name}…";

        var progress = new Progress<InstallProgress>(value =>
        {
            InstallProgressValue = value.Percent;
            InstallProgressText = value.Total > 0
                ? $"{value.Stage} · {value.Completed}/{value.Total}{(string.IsNullOrWhiteSpace(value.CurrentItem) ? string.Empty : $" · {value.CurrentItem}")}" 
                : value.Stage;
        });

        try
        {
            await _installer.InstallAsync(SelectedInstance, version, progress);
            InstallProgressValue = 100;
            InstallProgressText = "Vanilla files prepared";
            LauncherStatus = $"{SelectedInstance.Name} is prepared";
        }
        catch (Exception ex)
        {
            InstallProgressText = "Install failed";
            LauncherStatus = $"Install failed: {ex.Message}";
        }
        finally
        {
            IsInstallBusy = false;
        }
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
