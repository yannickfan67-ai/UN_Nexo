using System.Reflection;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using UN.Nexo.Core.Services;
using UN.Nexo.Desktop.ViewModels;

namespace UN.Nexo.Desktop.Views;

public sealed partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly NexoPathService _paths;
    private readonly MinecraftVanillaInstallService _installer;
    private Task? _initializationTask;
    private ServerHubWindow? _serverHub;
    private RuntimeSettingsWindow? _runtimeSettingsWindow;
    private InstanceRepairWindow? _repairWindow;
    private DownloadManagerWindow? _downloadManagerWindow;

    public MainWindow()
    {
        InitializeComponent();

        _paths = new NexoPathService();
        var downloadSources = new DownloadSourceService();
        var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        var launcherVersion = typeof(MainWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "dev";
        Title = $"UN_Nexo {launcherVersion}";
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"UN_Nexo/{launcherVersion}");
        ApplyRuntimeVersionLabel(launcherVersion);

        _installer = new MinecraftVanillaInstallService(httpClient, _paths, downloadSources);
        _viewModel = new MainWindowViewModel(
            new JavaDiscoveryService(_paths),
            _paths,
            new MinecraftVersionManifestService(httpClient, downloadSources),
            new InstanceStoreService(_paths),
            _installer,
            new AccountStoreService(_paths),
            new LauncherSettingsService(_paths),
            downloadSources);

        DataContext = _viewModel;
        AddUtilityNavigation();
        AddSessionOverlay();
        Opened += OnOpened;
    }

    public Task InitializeAsync()
        => _initializationTask ??= _viewModel.InitializeAsync();

    public async Task RevealAsync()
    {
        const int frames = 9;
        for (var frame = 1; frame <= frames; frame++)
        {
            Opacity = frame / (double)frames;
            await Task.Delay(18);
        }
        Opacity = 1;
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
        await InitializeAsync();
    }
}
