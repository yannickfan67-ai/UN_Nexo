using System.Reflection;
using Avalonia.Controls;
using UN.Nexo.Core.Services;
using UN.Nexo.Desktop.ViewModels;

namespace UN.Nexo.Desktop.Views;

public sealed partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private Task? _initializationTask;

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

    private async void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        await InitializeAsync();
    }
}
