using Avalonia.Controls;
using UN.Nexo.Core.Services;
using UN.Nexo.Desktop.ViewModels;

namespace UN.Nexo.Desktop.Views;

public sealed partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        var paths = new NexoPathService();
        var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("UN_Nexo/0.2.0-dev");

        _viewModel = new MainWindowViewModel(
            new JavaDiscoveryService(),
            paths,
            new MinecraftVersionManifestService(httpClient),
            new InstanceStoreService(paths),
            new MinecraftVanillaInstallService(httpClient, paths));

        DataContext = _viewModel;
        Opened += OnOpened;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        await _viewModel.InitializeAsync();
    }
}
