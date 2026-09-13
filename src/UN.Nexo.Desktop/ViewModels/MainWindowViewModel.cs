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

    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string launcherStatus = "Ready";
    [ObservableProperty] private string minecraftDirectory = "Detecting…";
    [ObservableProperty] private string instanceRoot = "Detecting…";
    [ObservableProperty] private string javaSummary = "Not scanned";
    [ObservableProperty] private string javaDetail = "Run environment scan to detect Java.";
    [ObservableProperty] private string latestRelease = "Unknown";
    [ObservableProperty] private string latestSnapshot = "Unknown";
    [ObservableProperty] private string instanceSummary = "0 instances";

    public ObservableCollection<JavaInstallation> JavaInstallations { get; } = [];

    public MainWindowViewModel(
        JavaDiscoveryService javaDiscovery,
        NexoPathService paths,
        MinecraftVersionManifestService manifest,
        InstanceStoreService instances)
    {
        _javaDiscovery = javaDiscovery;
        _paths = paths;
        _manifest = manifest;
        _instances = instances;
    }

    public Task InitializeAsync() => RefreshEnvironmentAsync();

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

        try
        {
            var javaTask = _javaDiscovery.DiscoverAsync();
            var versionTask = TryGetLatestVersionAsync();
            var instancesTask = _instances.GetAllAsync();

            var java = await javaTask;
            var versions = await versionTask;
            var instances = await instancesTask;

            JavaInstallations.Clear();
            foreach (var installation in java)
                JavaInstallations.Add(installation);

            JavaSummary = java.Count == 0 ? "No Java found" : $"{java.Count} Java installation{(java.Count == 1 ? string.Empty : "s")}";
            JavaDetail = java.Count == 0
                ? "Install Java 21+ or set JAVA_HOME. Nexo will later select Java per Minecraft version."
                : $"Preferred: Java {java[0].Version} · {(java[0].Is64Bit ? "64-bit" : "architecture unknown")}\n{java[0].JavaPath}";

            LatestRelease = versions?.LatestRelease ?? "Offline";
            LatestSnapshot = versions?.LatestSnapshot ?? "Offline";
            InstanceSummary = $"{instances.Count} instance{(instances.Count == 1 ? string.Empty : "s")}";
            LauncherStatus = "Environment ready";
        }
        catch (Exception ex)
        {
            LauncherStatus = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<MinecraftReleaseInfo?> TryGetLatestVersionAsync()
    {
        try
        {
            return await _manifest.GetLatestAsync();
        }
        catch
        {
            return null;
        }
    }
}
