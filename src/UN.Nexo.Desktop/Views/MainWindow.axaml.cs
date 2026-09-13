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
            Timeout = TimeSpan.FromSeconds(8)
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("UN_Nexo/0.1.0-dev");

        _viewModel = new MainWindowViewModel(
            new JavaDiscoveryService(),
            paths,
            new MinecraftVersionManifestService(httpClient),
            new InstanceStoreService(paths));

        DataContext = _viewModel;
        Opened += OnOpened;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        await _viewModel.InitializeAsync();
    }
}
