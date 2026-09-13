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
    private readonly MinecraftServerStatusService _statusService = new();
    private readonly MainWindowViewModel? _viewModel;
    private CancellationTokenSource? _statusCancellation;
    private ServerStatusResult? _lastStatus;

    public ServerHubWindow()
    {
        InitializeComponent();
        ServerList.ItemsSource = _servers;
        Opened += OnOpened;
        Closed += (_, _) =>
        {
            _statusCancellation?.Cancel();
            _statusCancellation?.Dispose();
            _statusCancellation = null;
        };
    }

    public ServerHubWindow(MainWindowViewModel viewModel) : this()
    {
        _viewModel = viewModel;
        DefaultInstanceBox.ItemsSource = viewModel.Instances;
        DefaultInstanceBox.SelectedItem = viewModel.SelectedInstance;
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

        _statusCancellation?.Cancel();
        await _store.RemoveAsync(favorite.Id);
        await ReloadAsync();
        ServerNameBox.Text = string.Empty;
        ServerAddressBox.Text = "localhost:25565";
        DefaultInstanceBox.SelectedItem = _viewModel?.SelectedInstance;
        ClearStatusCard("Select a saved server or enter an address, then refresh status.");
        ServerStatus.Text = $"Removed {favorite.Name}.";
    }

    private async void OnSaveDefaultInstanceClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ServerList.SelectedItem is not ServerFavorite favorite)
        {
            ServerStatus.Text = "Save or select a server before linking an instance.";
            return;
        }

        var instance = DefaultInstanceBox.SelectedItem as GameInstance;
        var updated = await _store.SetDefaultInstanceAsync(favorite.Id, instance?.Id);
        if (updated is null)
        {
            ServerStatus.Text = "The selected server no longer exists.";
            return;
        }

        await ReloadAsync(updated.Id);
        ServerStatus.Text = instance is null
            ? $"Cleared the default instance for {updated.Name}."
            : $"{updated.Name} will use {instance.Name} by default.";
        UpdateCompatibility();
    }

    private async void OnClearDefaultInstanceClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        DefaultInstanceBox.SelectedItem = null;
        OnSaveDefaultInstanceClicked(sender, e);
        await Task.CompletedTask;
    }

    private async void OnRefreshStatusClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await RefreshStatusAsync(ServerAddressBox.Text ?? string.Empty);

    private async Task RefreshStatusAsync(string address)
    {
        MinecraftServerTarget target;
        try
        {
            target = MinecraftServerTarget.Parse(address);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            _lastStatus = null;
            ClearStatusCard("Invalid server address.");
            ServerStatus.Text = ex.Message;
            return;
        }

        _statusCancellation?.Cancel();
        _statusCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _statusCancellation = cancellation;

        RefreshStatusButton.IsEnabled = false;
        StatusStateText.Text = "Checking…";
        StatusDetailText.Text = target.Authority;
        ServerStatus.Text = $"Checking {target.Authority}…";

        try
        {
            var status = await _statusService.QueryAsync(target, cancellation.Token);
            if (!ReferenceEquals(_statusCancellation, cancellation))
                return;

            _lastStatus = status;
            ApplyStatus(status);
            ServerStatus.Text = status.IsOnline
                ? $"{target.Authority} responded successfully."
                : $"{target.Authority}: {status.StateLabel}. {status.Error}";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (ReferenceEquals(_statusCancellation, cancellation))
                ServerStatus.Text = "Status check cancelled.";
        }
        finally
        {
            if (ReferenceEquals(_statusCancellation, cancellation))
            {
                RefreshStatusButton.IsEnabled = true;
                _statusCancellation = null;
                cancellation.Dispose();
            }
        }
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

        if (DefaultInstanceBox.SelectedItem is GameInstance requestedInstance)
            _viewModel.SelectedInstance = requestedInstance;
        else if (ServerList.SelectedItem is ServerFavorite { DefaultInstanceId: { Length: > 0 } instanceId })
        {
            var linkedInstance = _viewModel.Instances.FirstOrDefault(instance => instance.Id == instanceId);
            if (linkedInstance is null)
            {
                ServerStatus.Text = "This server points to an instance that no longer exists. Choose another instance and save the link.";
                return;
            }
            _viewModel.SelectedInstance = linkedInstance;
            DefaultInstanceBox.SelectedItem = linkedInstance;
        }

        if (_lastStatus is { IsOnline: true }
            && string.Equals(_lastStatus.Address, target.Authority, StringComparison.OrdinalIgnoreCase))
        {
            var compatibility = MinecraftProtocolCompatibility.Compare(_viewModel.SelectedInstance, _lastStatus);
            if (compatibility.State == ProtocolCompatibilityState.Mismatch)
            {
                CompatibilityText.Text = compatibility.Message;
                ServerStatus.Text = "Protocol mismatch. Choose a compatible instance before connecting.";
                return;
            }
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
        SaveDefaultInstanceButton.IsEnabled = favorite is not null;
        ClearDefaultInstanceButton.IsEnabled = favorite is not null;
        if (favorite is null)
            return;

        ServerNameBox.Text = favorite.Name;
        ServerAddressBox.Text = favorite.Address;
        DefaultInstanceBox.SelectedItem = _viewModel?.Instances.FirstOrDefault(instance => instance.Id == favorite.DefaultInstanceId)
            ?? _viewModel?.SelectedInstance;
        _ = RefreshStatusAsync(favorite.Address);
    }

    private void OnDefaultInstanceSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateCompatibility();
        UpdateSelectionSummary();
    }

    private void ApplyStatus(ServerStatusResult status)
    {
        StatusStateText.Text = status.StateLabel;
        StatusDetailText.Text = status.IsOnline
            ? status.Address
            : string.IsNullOrWhiteSpace(status.Error) ? status.Address : status.Error;
        LatencyText.Text = status.LatencyMs is { } latency ? $"{latency} ms" : "—";
        PlayersText.Text = status.OnlinePlayers is { } online && status.MaxPlayers is { } max
            ? $"{online} / {max}"
            : "—";
        VersionText.Text = string.IsNullOrWhiteSpace(status.VersionName)
            ? status.ProtocolVersion is { } protocol ? $"Protocol {protocol}" : "—"
            : status.ProtocolVersion is { } versionProtocol
                ? $"{status.VersionName} · protocol {versionProtocol}"
                : status.VersionName;
        MotdText.Text = string.IsNullOrWhiteSpace(status.Motd) ? "No MOTD supplied." : status.Motd;
        UpdateCompatibility();
    }

    private void UpdateCompatibility()
    {
        if (_lastStatus is null)
        {
            CompatibilityText.Text = "Refresh status to compare the selected instance protocol.";
            return;
        }

        var instance = DefaultInstanceBox.SelectedItem as GameInstance ?? _viewModel?.SelectedInstance;
        CompatibilityText.Text = MinecraftProtocolCompatibility.Compare(instance, _lastStatus).Message;
    }

    private void ClearStatusCard(string message)
    {
        StatusStateText.Text = "Not checked";
        StatusDetailText.Text = message;
        LatencyText.Text = "—";
        PlayersText.Text = "—";
        VersionText.Text = "—";
        MotdText.Text = "—";
        CompatibilityText.Text = "Refresh status to compare the selected instance protocol.";
    }

    private void UpdateSelectionSummary()
    {
        if (_viewModel is null)
            return;

        var selected = DefaultInstanceBox.SelectedItem as GameInstance ?? _viewModel.SelectedInstance;
        var instance = selected?.Name ?? "No instance";
        var account = _viewModel.SelectedAccount?.DisplayName ?? "No profile";
        SelectionSummary.Text = $"{instance} · {account}";
    }
}
