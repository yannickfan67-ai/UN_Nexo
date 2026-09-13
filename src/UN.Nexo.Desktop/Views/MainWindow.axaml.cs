using System.Reflection;
using Avalonia.Controls;
using Avalonia.LogicalTree;
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
        ApplyRuntimeVersionLabel(launcherVersion);

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
        AddServerNavigation();
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

    private void AddServerNavigation()
    {
        var nav = this.GetLogicalDescendants()
            .OfType<StackPanel>()
            .FirstOrDefault(panel =>
                panel.Children.OfType<Button>().Any(button => Equals(button.Content, "Home"))
                && panel.Children.OfType<Button>().Any(button => Equals(button.Content, "Instances")));
        if (nav is null || nav.Children.OfType<Button>().Any(button => Equals(button.Content, "Servers")))
            return;

        var servers = new Button { Content = "Servers" };
        servers.Classes.Add("nav");
        servers.Click += (_, _) => OpenServerHub();
        nav.Children.Insert(Math.Min(3, nav.Children.Count), servers);
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

    private async void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        await InitializeAsync();
    }
}
