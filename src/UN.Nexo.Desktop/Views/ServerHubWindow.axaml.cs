using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.Selection;
using UN.Nexo.Core.Launching;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;
using UN.Nexo.Desktop.ViewModels;

namespace UN.Nexo.Desktop.Views;

public sealed partial class ServerHubWindow : Window
{
    private readonly ObservableCollection<ServerFavorite> _servers = [];
    private readonly ServerStoreService _store = new(new NexoPathService());
    private readonly MainWindowViewModel? _viewModel;

    public ServerHubWindow()
    {
        InitializeComponent();
        ServerList.ItemsSource = _servers;
        Opened += OnOpened;
    }

    public ServerHubWindow(MainWindowViewModel viewModel) : this()
    {
        _viewModel = viewModel;
        UpdateSelectionSummary();
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        await ReloadAsync();
        UpdateSelectionSummary();
    }

    private async Task ReloadAsync(string? selectId = null)
    {
        var items = await _store.GetAllAsync();
        _servers.Clear();
        foreach (var item in items)
            _servers.Add(item);

        if (selectId is not null)
            ServerList.SelectedItem = _servers.FirstOrDefault(item => item.Id == selectId);
    }

    private async void OnSaveClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var favorite = await _store.AddAsync(ServerNameBox.Text ?? string.Empty, ServerAddressBox.Text ?? string.Empty);
            await ReloadAsync(favorite.Id);
            ServerStatus.Text = $"Saved {favorite.Name} · {favorite.Address}";
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IOException)
        {
            ServerStatus.Text = ex.Message;
        }
    }

    private async void OnRemoveClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ServerList.SelectedItem is not ServerFavorite favorite)
            return;

        await _store.RemoveAsync(favorite.Id);
        await ReloadAsync();
        ServerNameBox.Text = string.Empty;
        ServerAddressBox.Text = "localhost:25565";
        ServerStatus.Text = $"Removed {favorite.Name}.";
    }

    private async void OnLaunchClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            ServerStatus.Text = "Server launching is unavailable in the designer.";
            return;
        }

        MinecraftServerTarget target;
        try
        {
            target = MinecraftServerTarget.Parse(ServerAddressBox.Text ?? string.Empty);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            ServerStatus.Text = ex.Message;
            return;
        }

        if (!_viewModel.CanPlay)
        {
            ServerStatus.Text = "Select an instance and a local profile first. Nexo will prepare missing game files and Java automatically.";
            UpdateSelectionSummary();
            return;
        }

        ServerStatus.Text = $"Launching {target.Authority}…";
        await _viewModel.LaunchServerAsync(target.Authority);
        ServerStatus.Text = _viewModel.GameStatus;
        UpdateSelectionSummary();
    }

    private void OnServerSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var favorite = ServerList.SelectedItem as ServerFavorite;
        RemoveButton.IsEnabled = favorite is not null;
        if (favorite is null)
            return;

        ServerNameBox.Text = favorite.Name;
        ServerAddressBox.Text = favorite.Address;
    }

    private void UpdateSelectionSummary()
    {
        if (_viewModel is null)
            return;

        var instance = _viewModel.SelectedInstance?.Name ?? "No instance";
        var account = _viewModel.SelectedAccount?.DisplayName ?? "No profile";
        SelectionSummary.Text = $"{instance} · {account}";
    }
}
