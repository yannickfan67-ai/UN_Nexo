using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.Selection;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;
using UN.Nexo.Desktop.ViewModels;

namespace UN.Nexo.Desktop.Views;

public sealed partial class InstanceBackupWindow : Window
{
    private readonly ObservableCollection<WorldBackupInfo> _backups = [];
    private readonly MainWindowViewModel? _viewModel;
    private readonly InstanceLifecycleService _lifecycle;
    private readonly GameInstance? _instance;
    private CancellationTokenSource? _operationCancellation;
    private bool _busy;

    public InstanceBackupWindow()
    {
        InitializeComponent();
        _lifecycle = new InstanceLifecycleService(new NexoPathService());
        BackupList.ItemsSource = _backups;
        Opened += OnOpened;
        Closed += OnClosed;
    }

    public InstanceBackupWindow(MainWindowViewModel viewModel, NexoPathService paths) : this()
    {
        _viewModel = viewModel;
        _lifecycle = new InstanceLifecycleService(paths);
        _instance = viewModel.SelectedInstance;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        if (_instance is null)
        {
            InstanceSummary.Text = "No instance selected.";
            OperationStatus.Text = "Select an instance before opening Clone & backups.";
            SetBusy(true);
            return;
        }

        InstanceSummary.Text = $"{_instance.Name} · Minecraft {_instance.VersionId} · {_instance.Loader}";
        CloneNameBox.Text = $"{_instance.Name} copy";
        await ReloadBackupsAsync();
    }

    private void OnClosed(object? sender, EventArgs e)
        => _operationCancellation?.Cancel();

    private CancellationToken BeginOperation()
    {
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        SetBusy(true);
        return _operationCancellation.Token;
    }

    private void EndOperation()
    {
        _operationCancellation?.Dispose();
        _operationCancellation = null;
        SetBusy(false);
    }

    private async void OnRefreshClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await ReloadBackupsAsync();

    private async void OnCloneClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!CanStartOperation("clone"))
            return;

        var cancellationToken = BeginOperation();
        try
        {
            OperationStatus.Text = "Cloning instance to staging…";
            var clone = await _lifecycle.CloneAsync(
                _instance!,
                CloneNameBox.Text ?? string.Empty,
                IncludeWorldsBox.IsChecked == true,
                cancellationToken);

            if (_viewModel is not null)
            {
                _viewModel.Instances.Add(clone);
                _viewModel.SelectedInstance = clone;
                _viewModel.InstanceSummary = $"{_viewModel.Instances.Count} instance{(_viewModel.Instances.Count == 1 ? string.Empty : "s")}";
            }

            OperationStatus.Text = $"Created independent clone '{clone.Name}'.";
        }
        catch (OperationCanceledException)
        {
            OperationStatus.Text = "Clone cancelled; staging was cleaned up.";
        }
        catch (Exception ex)
        {
            OperationStatus.Text = $"Clone failed: {ex.Message}";
        }
        finally
        {
            EndOperation();
        }
    }

    private async void OnBackupClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await CreateBackupAsync("manual");

    private async void OnPreUpgradeBackupClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await CreateBackupAsync("pre-upgrade");

    private async Task CreateBackupAsync(string kind)
    {
        if (!CanStartOperation("back up worlds"))
            return;

        var cancellationToken = BeginOperation();
        try
        {
            OperationStatus.Text = kind == "pre-upgrade"
                ? "Creating pre-upgrade world backup…"
                : "Creating world backup…";
            var created = await _lifecycle.CreateWorldBackupAsync(
                _instance!,
                kind,
                cancellationToken);
            await ReloadBackupsAsync(created.Id);
            OperationStatus.Text = $"Backup complete · {created.Worlds.Count} world{(created.Worlds.Count == 1 ? string.Empty : "s")} · {created.ArchiveSizeLabel}.";
        }
        catch (OperationCanceledException)
        {
            OperationStatus.Text = "Backup cancelled; temporary archive was cleaned up.";
        }
        catch (Exception ex)
        {
            OperationStatus.Text = $"Backup failed: {ex.Message}";
        }
        finally
        {
            EndOperation();
        }
    }

    private async void OnRestoreClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!CanStartOperation("restore a world"))
            return;
        if (BackupList.SelectedItem is not WorldBackupInfo backup
            || WorldCombo.SelectedItem is not WorldBackupWorld world)
        {
            OperationStatus.Text = "Select a backup and world first.";
            return;
        }

        var cancellationToken = BeginOperation();
        try
        {
            OperationStatus.Text = $"Restoring {world.Name} through staging…";
            var result = await _lifecycle.RestoreWorldAsync(
                _instance!,
                backup,
                world.Name,
                cancellationToken);
            OperationStatus.Text = result.SafetyCopyPath is null
                ? $"Restored {result.WorldName}. No previous world needed a safety copy."
                : $"Restored {result.WorldName}. Previous world preserved at {result.SafetyCopyPath}";
        }
        catch (OperationCanceledException)
        {
            OperationStatus.Text = "Restore cancelled; staged world data was cleaned up.";
        }
        catch (Exception ex)
        {
            OperationStatus.Text = $"Restore failed: {ex.Message}";
        }
        finally
        {
            EndOperation();
        }
    }

    private void OnBackupSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (BackupList.SelectedItem is not WorldBackupInfo backup)
        {
            WorldCombo.ItemsSource = null;
            BackupPreview.Text = "Select a backup to preview its worlds.";
            RestoreButton.IsEnabled = false;
            return;
        }

        WorldCombo.ItemsSource = backup.Worlds;
        WorldCombo.SelectedIndex = backup.Worlds.Count > 0 ? 0 : -1;
        BackupPreview.Text = $"{backup.DisplayName}\n{backup.Worlds.Count} world{(backup.Worlds.Count == 1 ? string.Empty : "s")} · {backup.ArchiveSizeLabel}\n{backup.WorldsSummary}";
        RestoreButton.IsEnabled = !_busy && backup.Worlds.Count > 0;
    }

    private async Task ReloadBackupsAsync(string? selectId = null)
    {
        if (_instance is null || _busy)
            return;

        try
        {
            var backups = await _lifecycle.GetBackupsAsync(_instance);
            _backups.Clear();
            foreach (var backup in backups)
                _backups.Add(backup);

            BackupList.SelectedItem = selectId is null
                ? _backups.FirstOrDefault()
                : _backups.FirstOrDefault(item => item.Id == selectId) ?? _backups.FirstOrDefault();

            if (_backups.Count == 0)
                BackupPreview.Text = "No backups yet.";
        }
        catch (Exception ex)
        {
            OperationStatus.Text = $"Could not load backups: {ex.Message}";
        }
    }

    private bool CanStartOperation(string action)
    {
        if (_busy || _instance is null)
            return false;
        if (_viewModel?.IsGameRunning == true)
        {
            OperationStatus.Text = $"Stop Minecraft before you {action}. A running world can be inconsistent on disk.";
            return false;
        }
        if (_viewModel?.IsInstallBusy == true)
        {
            OperationStatus.Text = $"Wait for instance file preparation to finish before you {action}.";
            return false;
        }
        return true;
    }

    private void SetBusy(bool value)
    {
        _busy = value;
        CloneButton.IsEnabled = !value && _instance is not null;
        BackupButton.IsEnabled = !value && _instance is not null;
        PreUpgradeBackupButton.IsEnabled = !value && _instance is not null;
        RestoreButton.IsEnabled = !value
            && BackupList.SelectedItem is WorldBackupInfo backup
            && backup.Worlds.Count > 0;
    }
}
