using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using UN.Nexo.Core.Services;
using UN.Nexo.Desktop.ViewModels;

namespace UN.Nexo.Desktop.Views;

public sealed partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private Task? _initializationTask;
    private ServerHubWindow? _serverHub;

    public MainWindow()
    {
        InitializeComponent();

        var paths = new NexoPathService();
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

        _viewModel = new MainWindowViewModel(
            new JavaDiscoveryService(),
            paths,
            new MinecraftVersionManifestService(httpClient, downloadSources),
            new InstanceStoreService(paths),
            new MinecraftVanillaInstallService(httpClient, paths, downloadSources),
            new AccountStoreService(paths),
            new LauncherSettingsService(paths),
            downloadSources);

        DataContext = _viewModel;
        AddOverlays();
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

    private void AddOverlays()
    {
        if (Content is not Control shell)
            return;

        Content = null;
        var layers = new Grid();
        layers.Children.Add(shell);

        var servers = new Button
        {
            Content = "Servers",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 22, 22),
            Padding = new Thickness(16, 10),
            MinWidth = 100
        };
        servers.Classes.Add("primary");
        servers.Click += (_, _) => OpenServerHub();
        layers.Children.Add(servers);
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

    private async void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        await InitializeAsync();
    }
}
