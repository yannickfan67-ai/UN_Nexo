using Avalonia.Controls;
using Avalonia.Controls.Selection;
using Avalonia.Platform.Storage;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;
using UN.Nexo.Desktop.ViewModels;

namespace UN.Nexo.Desktop.Views;

public sealed partial class GameImportWindow : Window
{
    private readonly MainWindowViewModel? _viewModel;
    private readonly GameDirectoryImportService _importer;
    private GameDirectoryImportPreview? _preview;
    private CancellationTokenSource? _operationCancellation;
    private bool _busy;

    public GameImportWindow()
    {
        InitializeComponent();
        _importer = new GameDirectoryImportService(new NexoPathService());
    }

    public GameImportWindow(MainWindowViewModel viewModel, NexoPathService paths) : this()
    {
        _viewModel = viewModel;
        _importer = new GameDirectoryImportService(paths);
    }

    private async void OnBrowseClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_busy)
            return;
        if (!StorageProvider.CanPickFolder)
        {
            OperationStatus.Text = "This platform does not expose a folder picker. Paste the .minecraft/game path into the source box instead.";
            return;
        }

        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose .minecraft or Minecraft game directory",
                AllowMultiple = false
            });
            var folder = folders.FirstOrDefault();
            if (folder is null)
                return;
            var localPath = folder.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(localPath))
            {
                OperationStatus.Text = "The selected folder has no direct local filesystem path. Paste a local .minecraft/game path instead.";
                return;
            }
            SourcePathBox.Text = localPath;
            await ScanAsync();
        }
        catch (Exception ex)
        {
            OperationStatus.Text = $"Folder picker failed: {ex.Message}";
        }
    }

    private async void OnScanClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await ScanAsync();

    private async Task ScanAsync()
    {
        if (_busy)
            return;
        var path = SourcePathBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            OperationStatus.Text = "Choose or paste a .minecraft/game directory first.";
            return;
        }

        BeginOperation();
        try
        {
            OperationStatus.Text = "Scanning versions, loaders and reusable game data…";
            _preview = await _importer.ScanAsync(path, _operationCancellation!.Token);
            SourcePathBox.Text = _preview.SourceDirectory;
            VersionList.ItemsSource = _preview.Versions;
            var preferred = _preview.Versions.FirstOrDefault(item => item.IsSupportedLoader)
                ?? _preview.Versions.FirstOrDefault(item => item.Loader is "fabric" or "forge")
                ?? _preview.Versions.FirstOrDefault();
            VersionList.SelectedItem = preferred;
            ContentSummary.Text = $"{_preview.ContentSummary} · estimated import data {_preview.SizeLabel} · {_preview.Versions.Count} version profile{(_preview.Versions.Count == 1 ? string.Empty : "s")}";
            WarningsBox.Text = _preview.Warnings.Count == 0
                ? "No scan warnings."
                : string.Join(Environment.NewLine, _preview.Warnings.Select(item => "• " + item));
            if (preferred is not null && string.IsNullOrWhiteSpace(InstanceNameBox.Text))
                InstanceNameBox.Text = $"Imported {preferred.BaseVersionId}";
            OperationStatus.Text = _preview.HasVersions
                ? "Scan complete. Select the exact version/profile you want to migrate."
                : "Scan completed, but no usable version profile was found.";
        }
        catch (OperationCanceledException)
        {
            OperationStatus.Text = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            _preview = null;
            VersionList.ItemsSource = null;
            VersionDetail.Text = "Nothing selected.";
            ContentSummary.Text = "Scan failed.";
            WarningsBox.Text = string.Empty;
            OperationStatus.Text = $"Scan failed: {ex.Message}";
        }
        finally
        {
            EndOperation();
            RefreshImportAvailability();
        }
    }

    private void OnVersionSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (VersionList.SelectedItem is not ImportVersionCandidate candidate)
        {
            VersionDetail.Text = "Nothing selected.";
            RefreshImportAvailability();
            return;
        }

        VersionDetail.Text =
            $"Profile: {candidate.VersionId}\n" +
            $"Loader: {candidate.Loader}\n" +
            $"Base Minecraft: {candidate.BaseVersionId}\n" +
            $"Client/base jar present: {(candidate.HasClientJar ? "yes" : "no")}\n" +
            candidate.Detail + " " + candidate.SupportLabel + ".";

        if (string.IsNullOrWhiteSpace(InstanceNameBox.Text)
            || InstanceNameBox.Text.StartsWith("Imported ", StringComparison.Ordinal))
            InstanceNameBox.Text = candidate.Loader == "vanilla"
                ? $"Imported {candidate.VersionId}"
                : $"Imported {candidate.Loader} {candidate.BaseVersionId}";
        RefreshImportAvailability();
    }

    private async void OnImportClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_busy || _preview is null || VersionList.SelectedItem is not ImportVersionCandidate candidate)
            return;
        if (candidate.Loader == "unsupported")
        {
            OperationStatus.Text = "That loader/profile is unsupported. Select a Vanilla, Fabric or Forge profile.";
            return;
        }
        if (_viewModel?.IsGameRunning == true)
        {
            OperationStatus.Text = "Stop Minecraft before importing another game directory.";
            return;
        }
        if (_viewModel?.IsInstallBusy == true)
        {
            OperationStatus.Text = "Wait for the current instance preparation to finish before importing.";
            return;
        }

        BeginOperation();
        try
        {
            OperationStatus.Text = "Copying source data into an isolated staging instance…";
            var result = await _importer.ImportAsync(
                _preview,
                candidate,
                InstanceNameBox.Text ?? string.Empty,
                _operationCancellation!.Token);

            if (_viewModel is not null)
            {
                _viewModel.Instances.Add(result.Instance);
                _viewModel.SelectedInstance = result.Instance;
                _viewModel.InstanceSummary = $"{_viewModel.Instances.Count} instance{(_viewModel.Instances.Count == 1 ? string.Empty : "s")}";
            }

            var resultWarnings = result.Warnings.Count == 0
                ? string.Empty
                : Environment.NewLine + string.Join(Environment.NewLine, result.Warnings.Select(item => "• " + item));
            WarningsBox.Text = result.Warnings.Count == 0
                ? "No import warnings."
                : string.Join(Environment.NewLine, result.Warnings.Select(item => "• " + item));
            OperationStatus.Text = result.PreparedFromExistingFiles
                ? $"Imported '{result.Instance.Name}' · {result.CopiedFiles} files · verified existing Vanilla files are ready to launch.{resultWarnings}"
                : $"Imported '{result.Instance.Name}' · {result.CopiedFiles} files. Existing data was preserved; additional preparation/loader support may still be required.{resultWarnings}";
        }
        catch (OperationCanceledException)
        {
            OperationStatus.Text = "Import cancelled. No partial instance was published.";
        }
        catch (Exception ex)
        {
            OperationStatus.Text = $"Import failed: {ex.Message}";
        }
        finally
        {
            EndOperation();
            RefreshImportAvailability();
        }
    }

    private void OnCancelClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _operationCancellation?.Cancel();

    private void BeginOperation()
    {
        _busy = true;
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        ScanButton.IsEnabled = false;
        ImportButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
    }

    private void EndOperation()
    {
        _busy = false;
        _operationCancellation?.Dispose();
        _operationCancellation = null;
        ScanButton.IsEnabled = true;
        CancelButton.IsEnabled = false;
    }

    private void RefreshImportAvailability()
    {
        ImportButton.IsEnabled = !_busy
            && _preview is not null
            && VersionList.SelectedItem is ImportVersionCandidate candidate
            && candidate.Loader != "unsupported";
    }
}
