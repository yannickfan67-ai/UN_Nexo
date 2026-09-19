using System.Reflection;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using UN.Nexo.Core.Services;
using UN.Nexo.Desktop.Diagnostics;
using UN.Nexo.Desktop.ViewModels;

namespace UN.Nexo.Desktop.Views;

public sealed partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly NexoPathService _paths;
    private readonly MinecraftVanillaInstallService _installer;
    private readonly MinecraftVersionManifestService _manifest;
    private readonly InstanceStoreService _instances;
    private readonly FabricMetaService _fabricMeta;
    private readonly FabricInstallService _fabricInstaller;
    private readonly QuiltMetaService _quiltMeta;
    private readonly QuiltInstallService _quiltInstaller;
    private readonly ForgeMetaService _forgeMeta;
    private readonly ForgeInstallService _forgeInstaller;
    private Task? _initializationTask;
    private ServerHubWindow? _serverHub;
    private RuntimeSettingsWindow? _runtimeSettingsWindow;
    private InstanceRepairWindow? _repairWindow;
    private InstanceBackupWindow? _backupWindow;
    private GameImportWindow? _importWindow;
    private FabricManagerWindow? _fabricWindow;
    private QuiltManagerWindow? _quiltWindow;
    private ForgeManagerWindow? _forgeWindow;
    private ModManagerWindow? _modWindow;
    private DownloadManagerWindow? _downloadManagerWindow;

    public MainWindow()
    {
        LauncherStartupTrace.Write("[startup] MainWindow.InitializeComponent begin");
        InitializeComponent();
        LauncherStartupTrace.Write("[startup] MainWindow.InitializeComponent complete");

        _paths = new NexoPathService();
        var downloadSources = new DownloadSourceService();
        var downloadHttpClient = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false
        })
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        var manifestHttpClient = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false
        })
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        var authHttpClient = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false
        })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        var regionHttpClient = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false
        })
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var launcherVersion = typeof(MainWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "dev";
        var testOfflineMode = LauncherLaunchMode.IsTestOfflineEnabled(
            Environment.GetCommandLineArgs().Skip(1));
        Title = testOfflineMode
            ? $"UN_Nexo {launcherVersion} [TEST OFFLINE]"
            : $"UN_Nexo {launcherVersion}";
        if (testOfflineMode)
            LauncherStartupTrace.Write("[startup] TEST OFFLINE MODE enabled by explicit --test-offline switch");
        downloadHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"UN_Nexo/{launcherVersion}");
        manifestHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"UN_Nexo/{launcherVersion}");
        authHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"UN_Nexo/{launcherVersion}");
        regionHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"UN_Nexo/{launcherVersion}");
        ApplyRuntimeVersionLabel(launcherVersion);

        LauncherStartupTrace.Write("[startup] Creating launcher services");
        _installer = new MinecraftVanillaInstallService(downloadHttpClient, _paths, downloadSources);
        _manifest = new MinecraftVersionManifestService(manifestHttpClient, downloadSources);
        _instances = new InstanceStoreService(_paths);
        _fabricMeta = new FabricMetaService(manifestHttpClient);
        _fabricInstaller = new FabricInstallService(
            downloadHttpClient,
            _paths,
            _installer,
            _fabricMeta);
        _quiltMeta = new QuiltMetaService(manifestHttpClient);
        _quiltInstaller = new QuiltInstallService(
            downloadHttpClient,
            _paths,
            _installer,
            _quiltMeta);
        _forgeMeta = new ForgeMetaService(manifestHttpClient);
        _forgeInstaller = new ForgeInstallService(
            downloadHttpClient,
            _paths,
            _installer);
        var accountStore = new AccountStoreService(_paths);
        var microsoftAuth = new MicrosoftMinecraftAuthService(
            authHttpClient,
            accountStore,
            new MsalMicrosoftAccessTokenProvider(_paths));
        _viewModel = new MainWindowViewModel(
            new JavaDiscoveryService(_paths),
            _paths,
            _manifest,
            _instances,
            _installer,
            _fabricInstaller,
            _quiltInstaller,
            _forgeInstaller,
            accountStore,
            microsoftAuth,
            new RestrictedRegionService(regionHttpClient),
            new LauncherSettingsService(_paths),
            downloadSources,
            testOfflineMode);

        DataContext = _viewModel;
        AddUtilityNavigation();
        AddSessionOverlay();
        Opened += OnOpened;
        LauncherStartupTrace.Write("[startup] MainWindow services and visual helpers ready");
    }

    public Task InitializeAsync()
    {
        if (_initializationTask is null)
        {
            LauncherStartupTrace.Write("[startup] MainWindow.InitializeAsync task created");
            _initializationTask = _viewModel.InitializeAsync();
        }
        return _initializationTask;
    }

    public async Task RevealAsync()
    {
        LauncherStartupTrace.Write("[startup] MainWindow reveal animation begin");
        const int frames = 9;
        for (var frame = 1; frame <= frames; frame++)
        {
            Opacity = frame / (double)frames;
            await Task.Delay(18);
        }
        Opacity = 1;
        LauncherStartupTrace.Write("[startup] MainWindow reveal animation complete");
    }

    private void ApplyRuntimeVersionLabel(string launcherVersion)
    {
        var label = this.GetLogicalDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(item => string.Equals(item.Text, "v0.4.0-dev", StringComparison.Ordinal));
        if (label is not null)
            label.Text = $"v{launcherVersion}";
    }

    private void AddUtilityNavigation()
    {
        var nav = this.GetLogicalDescendants()
            .OfType<StackPanel>()
            .FirstOrDefault(panel =>
                panel.Children.OfType<Button>().Any(button => Equals(button.Content, "Home"))
                && panel.Children.OfType<Button>().Any(button => Equals(button.Content, "Instances")));
        if (nav is null)
            return;

        if (!nav.Children.OfType<Button>().Any(button => Equals(button.Content, "Servers")))
        {
            var servers = new Button { Content = "Servers" };
            servers.Classes.Add("nav");
            servers.Click += (_, _) => OpenServerHub();
            nav.Children.Insert(Math.Min(3, nav.Children.Count), servers);
        }

        if (!nav.Children.OfType<Button>().Any(button => Equals(button.Content, "Fabric")))
        {
            var fabric = new Button { Content = "Fabric" };
            fabric.Classes.Add("nav");
            fabric.Click += (_, _) => OpenFabric();
            var instancesIndex = nav.Children
                .Select((child, index) => (child, index))
                .FirstOrDefault(pair => pair.child is Button button && Equals(button.Content, "Instances")).index;
            nav.Children.Insert(Math.Min(instancesIndex + 1, nav.Children.Count), fabric);
        }

        if (!nav.Children.OfType<Button>().Any(button => Equals(button.Content, "Quilt")))
        {
            var quilt = new Button { Content = "Quilt" };
            quilt.Classes.Add("nav");
            quilt.Click += (_, _) => OpenQuilt();
            var fabricIndex = nav.Children
                .Select((child, index) => (child, index))
                .FirstOrDefault(pair => pair.child is Button button && Equals(button.Content, "Fabric")).index;
            nav.Children.Insert(Math.Min(fabricIndex + 1, nav.Children.Count), quilt);
        }

        if (!nav.Children.OfType<Button>().Any(button => Equals(button.Content, "Forge")))
        {
            var forge = new Button { Content = "Forge" };
            forge.Classes.Add("nav");
            forge.Click += (_, _) => OpenForge();
            var quiltIndex = nav.Children
                .Select((child, index) => (child, index))
                .FirstOrDefault(pair => pair.child is Button button && Equals(button.Content, "Quilt")).index;
            nav.Children.Insert(Math.Min(quiltIndex + 1, nav.Children.Count), forge);
        }

        if (!nav.Children.OfType<Button>().Any(button => Equals(button.Content, "Mods")))
        {
            var mods = new Button { Content = "Mods" };
            mods.Classes.Add("nav");
            mods.Click += (_, _) => OpenMods();
            var forgeIndex = nav.Children
                .Select((child, index) => (child, index))
                .FirstOrDefault(pair => pair.child is Button button && Equals(button.Content, "Forge")).index;
            nav.Children.Insert(Math.Min(forgeIndex + 1, nav.Children.Count), mods);
        }

        if (!nav.Children.OfType<Button>().Any(button => Equals(button.Content, "Import")))
        {
            var import = new Button { Content = "Import" };
            import.Classes.Add("nav");
            import.Click += (_, _) => OpenImport();
            var modsIndex = nav.Children
                .Select((child, index) => (child, index))
                .FirstOrDefault(pair => pair.child is Button button && Equals(button.Content, "Mods")).index;
            nav.Children.Insert(Math.Min(modsIndex + 1, nav.Children.Count), import);
        }

        if (!nav.Children.OfType<Button>().Any(button => Equals(button.Content, "Runtime")))
        {
            var runtime = new Button { Content = "Runtime" };
            runtime.Classes.Add("nav");
            runtime.Click += (_, _) => OpenRuntimeSettings();
            var downloadsIndex = nav.Children
                .Select((child, index) => (child, index))
                .FirstOrDefault(pair => pair.child is Button button && Equals(button.Content, "Downloads")).index;
            nav.Children.Insert(downloadsIndex > 0 ? downloadsIndex : Math.Min(4, nav.Children.Count), runtime);
        }

        var downloads = nav.Children
            .OfType<Button>()
            .FirstOrDefault(button => Equals(button.Content, "Downloads"));
        if (downloads is not null)
        {
            downloads.IsHitTestVisible = true;
            downloads.Opacity = 1;
            downloads.Click += (_, _) => OpenDownloads();
        }

        if (!nav.Children.OfType<Button>().Any(button => Equals(button.Content, "Backups")))
        {
            var backups = new Button { Content = "Backups" };
            backups.Classes.Add("nav");
            backups.Click += (_, _) => OpenBackups();
            var settingsIndex = nav.Children
                .Select((child, index) => (child, index))
                .FirstOrDefault(pair => pair.child is Button button && Equals(button.Content, "Settings")).index;
            nav.Children.Insert(settingsIndex > 0 ? settingsIndex : nav.Children.Count, backups);
        }

        if (!nav.Children.OfType<Button>().Any(button => Equals(button.Content, "Repair")))
        {
            var repair = new Button { Content = "Repair" };
            repair.Classes.Add("nav");
            repair.Click += (_, _) => OpenRepair();
            var settingsIndex = nav.Children
                .Select((child, index) => (child, index))
                .FirstOrDefault(pair => pair.child is Button button && Equals(button.Content, "Settings")).index;
            nav.Children.Insert(settingsIndex > 0 ? settingsIndex : nav.Children.Count, repair);
        }
    }

    private void AddSessionOverlay()
    {
        if (Content is not Control shell)
            return;

        Content = null;
        var layers = new Grid();
        layers.Children.Add(shell);
        layers.Children.Add(new LaunchStatusOverlay());
        Content = layers;
    }

    private void OpenServerHub()
    {
        if (_serverHub is not null)
        {
            _serverHub.Activate();
            return;
        }

        _serverHub = new ServerHubWindow(_viewModel);
        _serverHub.Closed += (_, _) => _serverHub = null;
        _serverHub.Show(this);
    }

    private void OpenFabric()
    {
        if (_fabricWindow is not null)
        {
            _fabricWindow.Activate();
            return;
        }

        _fabricWindow = new FabricManagerWindow(
            _viewModel,
            _manifest,
            _instances,
            _fabricMeta,
            _fabricInstaller,
            _installer);
        _fabricWindow.Closed += (_, _) => _fabricWindow = null;
        _fabricWindow.Show(this);
    }

    private void OpenQuilt()
    {
        if (_quiltWindow is not null)
        {
            _quiltWindow.Activate();
            return;
        }

        _quiltWindow = new QuiltManagerWindow(
            _viewModel,
            _manifest,
            _instances,
            _quiltMeta,
            _quiltInstaller,
            _installer);
        _quiltWindow.Closed += (_, _) => _quiltWindow = null;
        _quiltWindow.Show(this);
    }

    private void OpenForge()
    {
        if (_forgeWindow is not null)
        {
            _forgeWindow.Activate();
            return;
        }

        _forgeWindow = new ForgeManagerWindow(
            _viewModel,
            _manifest,
            _instances,
            _forgeMeta,
            _forgeInstaller,
            _installer);
        _forgeWindow.Closed += (_, _) => _forgeWindow = null;
        _forgeWindow.Show(this);
    }

    private void OpenMods()
    {
        if (_modWindow is not null)
        {
            _modWindow.Activate();
            return;
        }

        _modWindow = new ModManagerWindow(_viewModel, _paths);
        _modWindow.Closed += (_, _) => _modWindow = null;
        _modWindow.Show(this);
    }

    private void OpenImport()
    {
        if (_importWindow is not null)
        {
            _importWindow.Activate();
            return;
        }

        _importWindow = new GameImportWindow(_viewModel, _paths);
        _importWindow.Closed += (_, _) => _importWindow = null;
        _importWindow.Show(this);
    }

    private void OpenRuntimeSettings()
    {
        if (_runtimeSettingsWindow is not null)
        {
            _runtimeSettingsWindow.Activate();
            return;
        }

        _runtimeSettingsWindow = new RuntimeSettingsWindow(_viewModel, _paths);
        _runtimeSettingsWindow.Closed += (_, _) => _runtimeSettingsWindow = null;
        _runtimeSettingsWindow.Show(this);
    }

    private void OpenDownloads()
    {
        if (_downloadManagerWindow is not null)
        {
            _downloadManagerWindow.Activate();
            return;
        }

        _downloadManagerWindow = new DownloadManagerWindow(_installer, RetrySelectedInstanceAsync);
        _downloadManagerWindow.Closed += (_, _) => _downloadManagerWindow = null;
        _downloadManagerWindow.Show(this);
    }

    private async Task RetrySelectedInstanceAsync()
        => await _viewModel.PrepareSelectedInstanceCommand.ExecuteAsync(null);

    private void OpenBackups()
    {
        if (_backupWindow is not null)
        {
            _backupWindow.Activate();
            return;
        }

        _backupWindow = new InstanceBackupWindow(_viewModel, _paths);
        _backupWindow.Closed += (_, _) => _backupWindow = null;
        _backupWindow.Show(this);
    }

    private void OpenRepair()
    {
        if (_repairWindow is not null)
        {
            _repairWindow.Activate();
            return;
        }

        _repairWindow = new InstanceRepairWindow(_viewModel, _paths);
        _repairWindow.Closed += (_, _) => _repairWindow = null;
        _repairWindow.Show(this);
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        LauncherStartupTrace.Write("[startup] MainWindow Opened event");
        try
        {
            await InitializeAsync();
            LauncherStartupTrace.Write("[startup] MainWindow Opened initialization await completed");
        }
        catch (Exception ex)
        {
            LauncherStartupTrace.Failure("MainWindow Opened initialization", ex);
        }
    }
}
