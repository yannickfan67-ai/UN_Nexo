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
    private Task? _initializationTask;
    private ServerHubWindow? _serverHub;
    private RuntimeSettingsWindow? _runtimeSettingsWindow;

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

        _viewModel = new MainWindowViewModel(
            new JavaDiscoveryService(),
            _paths,
            new MinecraftVersionManifestService(httpClient, downloadSources),
            new InstanceStoreService(_paths),
            new MinecraftVanillaInstallService(httpClient, _paths, downloadSources),
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

    private async void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        await InitializeAsync();
    }
}
