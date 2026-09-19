using Avalonia.Controls;
using Avalonia.Controls.Selection;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;
using UN.Nexo.Desktop.ViewModels;

namespace UN.Nexo.Desktop.Views;

public sealed partial class ForgeManagerWindow : Window
{
    private readonly MainWindowViewModel? _viewModel;
    private readonly MinecraftVersionManifestService? _manifest;
    private readonly InstanceStoreService? _instances;
    private readonly ForgeMetaService? _forgeMeta;
    private readonly ForgeInstallService? _forgeInstaller;
    private readonly MinecraftVanillaInstallService? _vanillaInstaller;
    private IReadOnlyList<MinecraftVersionInfo> _catalogVersions = [];
    private CancellationTokenSource? _operationCancellation;
    private CancellationTokenSource? _loaderQueryCancellation;
    private bool _busy;

    public ForgeManagerWindow()
    {
        InitializeComponent();
    }

    public ForgeManagerWindow(
        MainWindowViewModel viewModel,
        MinecraftVersionManifestService manifest,
        InstanceStoreService instances,
        ForgeMetaService forgeMeta,
        ForgeInstallService forgeInstaller,
        MinecraftVanillaInstallService vanillaInstaller) : this()
    {
        _viewModel = viewModel;
        _manifest = manifest;
        _instances = instances;
        _forgeMeta = forgeMeta;
        _forgeInstaller = forgeInstaller;
        _vanillaInstaller = vanillaInstaller;
        Opened += OnOpened;
        Closed += (_, _) =>
        {
            _operationCancellation?.Cancel();
            _loaderQueryCancellation?.Cancel();
            _operationCancellation?.Dispose();
            _loaderQueryCancellation?.Dispose();
        };
    }

    private async void OnOpened(
        object? sender,
        EventArgs e)
    {
        Opened -= OnOpened;
        await LoadCatalogAsync();
        await RefreshExistingAsync();
    }

    private async Task LoadCatalogAsync()
    {
        if (_manifest is null)
            return;

        try
        {
            OperationStatus.Text =
                "Loading Minecraft catalog…";
            var catalog =
                await _manifest.GetCatalogAsync();
            _catalogVersions =
                catalog.Versions
                    .Where(item =>
                        item.Type.Equals(
                            "release",
                            StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            MinecraftVersionBox.ItemsSource =
                _catalogVersions;
            MinecraftVersionBox.SelectedItem =
                _catalogVersions.FirstOrDefault(item =>
                    item.Id == catalog.Latest.LatestRelease)
                ?? _catalogVersions.FirstOrDefault();
            OperationStatus.Text =
                $"Minecraft catalog ready · {_catalogVersions.Count} releases.";
        }
        catch (Exception ex)
        {
            _catalogVersions = [];
            MinecraftVersionBox.ItemsSource = null;
            LoaderStatus.Text =
                "Minecraft catalog unavailable.";
            OperationStatus.Text =
                $"Catalog load failed: {ex.Message}";
        }

        RefreshCreateAvailability();
    }

    private async void OnMinecraftVersionChanged(
        object? sender,
        SelectionChangedEventArgs e)
    {
        await QueryLoaderVersionsAsync();
        UpdateSuggestedName();
    }

    private void OnLoaderVersionChanged(
        object? sender,
        SelectionChangedEventArgs e)
    {
        UpdateSuggestedName();
        RefreshCreateAvailability();
    }

    private async Task QueryLoaderVersionsAsync()
    {
        _loaderQueryCancellation?.Cancel();
        _loaderQueryCancellation?.Dispose();
        _loaderQueryCancellation =
            new CancellationTokenSource();
        LoaderVersionBox.ItemsSource = null;
        LoaderVersionBox.SelectedItem = null;

        if (_forgeMeta is null
            || MinecraftVersionBox.SelectedItem
                is not MinecraftVersionInfo version)
        {
            LoaderStatus.Text =
                "Choose a Minecraft release.";
            RefreshCreateAvailability();
            return;
        }

        var requestedVersion =
            version.Id;
        LoaderStatus.Text =
            $"Querying Forge promotions for Minecraft {requestedVersion}…";
        RefreshCreateAvailability();

        try
        {
            var loaders =
                await _forgeMeta.GetVersionsAsync(
                    requestedVersion,
                    _loaderQueryCancellation.Token);

            if (MinecraftVersionBox.SelectedItem
                    is not MinecraftVersionInfo current
                || !current.Id.Equals(
                    requestedVersion,
                    StringComparison.Ordinal))
            {
                return;
            }

            LoaderVersionBox.ItemsSource =
                loaders;
            LoaderVersionBox.SelectedItem =
                loaders.FirstOrDefault(item =>
                    item.IsRecommended)
                ?? loaders.FirstOrDefault(item =>
                    item.IsLatest)
                ?? loaders.FirstOrDefault();

            LoaderStatus.Text =
                loaders.Count == 0
                    ? $"Forge promotions report no latest/recommended build for Minecraft {requestedVersion}."
                    : $"{loaders.Count} Forge build{(loaders.Count == 1 ? string.Empty : "s")} · recommended preferred.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LoaderStatus.Text =
                $"Forge version query failed: {ex.Message}";
        }
        finally
        {
            RefreshCreateAvailability();
        }
    }

    private void UpdateSuggestedName()
    {
        if (MinecraftVersionBox.SelectedItem
            is not MinecraftVersionInfo version)
        {
            return;
        }

        var suggested =
            $"Forge {version.Id}";
        if (string.IsNullOrWhiteSpace(
                InstanceNameBox.Text)
            || InstanceNameBox.Text.StartsWith(
                "Forge ",
                StringComparison.Ordinal))
        {
            InstanceNameBox.Text =
                suggested;
        }
    }

    private async void OnCreateClicked(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_busy
            || _viewModel is null
            || _instances is null
            || _forgeInstaller is null
            || MinecraftVersionBox.SelectedItem
                is not MinecraftVersionInfo minecraft
            || LoaderVersionBox.SelectedItem
                is not ForgeLoaderVersion loader)
        {
            return;
        }

        if (!CanStartPreparation())
            return;

        var name =
            (InstanceNameBox.Text ?? string.Empty)
                .Trim();
        if (name.Length is < 1 or > 80
            || name.Any(char.IsControl))
        {
            OperationStatus.Text =
                "Instance name must be 1-80 printable characters.";
            return;
        }

        var existing =
            await _instances.GetAllAsync();
        if (existing.Any(item =>
                item.Name.Equals(
                    name,
                    StringComparison.OrdinalIgnoreCase)))
        {
            OperationStatus.Text =
                $"An instance named '{name}' already exists.";
            return;
        }

        BeginOperation();
        try
        {
            var launchId =
                ForgeMetaService.GetLaunchVersionId(
                    minecraft.Id,
                    loader.Version);
            OperationStatus.Text =
                $"Creating Forge {loader.Version} over Minecraft {minecraft.Id}…";

            var instance =
                await _instances.CreateAsync(
                    name,
                    launchId,
                    "forge",
                    minecraft.Id,
                    loader.Version,
                    _operationCancellation!.Token);
            AddOrSelectInMain(instance);

            await PrepareForgeAsync(
                instance,
                minecraft);

            OperationStatus.Text =
                $"Created and prepared '{instance.Name}' · Minecraft {minecraft.Id} · Forge {loader.Version}. It is ready to launch.";
            await RefreshExistingAsync(
                instance.Id);
        }
        catch (OperationCanceledException)
        {
            OperationStatus.Text =
                "Forge creation/preparation cancelled. A created instance may remain and can be prepared again from the list below.";
            await RefreshExistingAsync();
        }
        catch (Exception ex)
        {
            OperationStatus.Text =
                $"Forge creation/preparation failed: {ex.Message}";
            await RefreshExistingAsync();
        }
        finally
        {
            EndOperation();
        }
    }

    private async void OnPrepareExistingClicked(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_busy
            || _forgeInstaller is null
            || ExistingForgeList.SelectedItem
                is not GameInstance instance)
        {
            return;
        }

        if (!CanStartPreparation())
            return;

        BeginOperation();
        try
        {
            var baseId =
                instance.BaseVersionId;
            if (string.IsNullOrWhiteSpace(baseId))
            {
                baseId =
                    await _forgeInstaller.GetBaseVersionIdAsync(
                        instance,
                        _operationCancellation!.Token);
            }

            if (string.IsNullOrWhiteSpace(baseId))
            {
                throw new InvalidDataException(
                    "Forge profile does not identify its base Minecraft version.");
            }

            var baseVersion =
                _catalogVersions.FirstOrDefault(item =>
                    item.Id == baseId);
            if (baseVersion is null)
            {
                await LoadCatalogAsync();
                baseVersion =
                    _catalogVersions.FirstOrDefault(item =>
                        item.Id == baseId);
            }

            if (baseVersion is null)
            {
                throw new InvalidOperationException(
                    $"Minecraft {baseId} is not present in the current version catalog.");
            }

            OperationStatus.Text =
                $"Preparing '{instance.Name}' · Forge over Minecraft {baseId}…";
            await PrepareForgeAsync(
                instance,
                baseVersion);
            AddOrSelectInMain(instance);
            OperationStatus.Text =
                $"'{instance.Name}' is prepared and ready to launch.";
            await RefreshExistingAsync(
                instance.Id);
        }
        catch (OperationCanceledException)
        {
            OperationStatus.Text =
                "Forge preparation cancelled. Existing instance files were kept.";
        }
        catch (Exception ex)
        {
            OperationStatus.Text =
                $"Forge preparation failed: {ex.Message}";
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task PrepareForgeAsync(
        GameInstance instance,
        MinecraftVersionInfo baseVersion)
    {
        if (_forgeInstaller is null)
        {
            throw new InvalidOperationException(
                "Forge installer is unavailable.");
        }

        var progress =
            new Progress<InstallProgress>(value =>
            {
                ProgressBar.Value =
                    value.Percent;
                ProgressText.Text =
                    value.Total > 0
                        ? $"{value.Stage} · {value.Completed}/{value.Total}"
                          + (string.IsNullOrWhiteSpace(
                                  value.CurrentItem)
                              ? string.Empty
                              : $" · {value.CurrentItem}")
                        : value.Stage;
            });

        ProgressBar.Value = 0;
        ProgressText.Text =
            "Starting Forge preparation…";
        await _forgeInstaller.PrepareAsync(
            instance,
            baseVersion,
            progress,
            _operationCancellation!.Token);
        ProgressBar.Value = 100;
        ProgressText.Text =
            "Forge files prepared.";
    }

    private bool CanStartPreparation()
    {
        if (_viewModel?.IsGameRunning == true)
        {
            OperationStatus.Text =
                "Stop Minecraft before changing Forge instance files.";
            return false;
        }

        if (_viewModel?.IsInstallBusy == true
            || _vanillaInstaller?.IsInstalling == true)
        {
            OperationStatus.Text =
                "Wait for the current Minecraft file preparation task to finish.";
            return false;
        }

        return true;
    }

    private void OnExistingSelectionChanged(
        object? sender,
        SelectionChangedEventArgs e)
    {
        if (ExistingForgeList.SelectedItem
            is not GameInstance instance)
        {
            ExistingDetail.Text =
                "Select an existing Forge instance.";
            PrepareExistingButton.IsEnabled = false;
            return;
        }

        ExistingDetail.Text =
            $"Launch profile: {instance.VersionId}\n"
            + $"Base Minecraft: {(string.IsNullOrWhiteSpace(instance.BaseVersionId) ? "read from profile when prepared" : instance.BaseVersionId)}\n"
            + $"Forge: {(string.IsNullOrWhiteSpace(instance.LoaderVersion) ? "missing loader version" : instance.LoaderVersion)}";
        PrepareExistingButton.IsEnabled =
            !_busy
            && !string.IsNullOrWhiteSpace(
                instance.LoaderVersion);
    }

    private async Task RefreshExistingAsync(
        string? selectId = null)
    {
        if (_instances is null)
            return;

        var all =
            await _instances.GetAllAsync();
        var forge =
            all.Where(item =>
                    item.Loader.Equals(
                        "forge",
                        StringComparison.OrdinalIgnoreCase))
                .OrderBy(
                    item => item.Name,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();

        ExistingForgeList.ItemsSource =
            forge;
        var wanted =
            selectId
            ?? (ExistingForgeList.SelectedItem
                as GameInstance)?.Id;
        ExistingForgeList.SelectedItem =
            forge.FirstOrDefault(item =>
                item.Id == wanted)
            ?? forge.FirstOrDefault();
    }

    private void AddOrSelectInMain(
        GameInstance instance)
    {
        if (_viewModel is null)
            return;

        var existing =
            _viewModel.Instances.FirstOrDefault(
                item =>
                    item.Id == instance.Id);
        if (existing is null)
        {
            _viewModel.Instances.Add(instance);
            existing = instance;
        }

        _viewModel.SelectedInstance =
            existing;
        _viewModel.InstanceSummary =
            $"{_viewModel.Instances.Count} instance{(_viewModel.Instances.Count == 1 ? string.Empty : "s")}";
    }

    private void OnCancelClicked(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
        => _operationCancellation?.Cancel();

    private void BeginOperation()
    {
        _busy = true;
        _operationCancellation?.Dispose();
        _operationCancellation =
            new CancellationTokenSource();
        CreateButton.IsEnabled = false;
        PrepareExistingButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
    }

    private void EndOperation()
    {
        _busy = false;
        _operationCancellation?.Dispose();
        _operationCancellation = null;
        CancelButton.IsEnabled = false;
        RefreshCreateAvailability();
        PrepareExistingButton.IsEnabled =
            ExistingForgeList.SelectedItem
                is GameInstance selected
            && !string.IsNullOrWhiteSpace(
                selected.LoaderVersion);
    }

    private void RefreshCreateAvailability()
    {
        CreateButton.IsEnabled =
            !_busy
            && MinecraftVersionBox.SelectedItem
                is MinecraftVersionInfo
            && LoaderVersionBox.SelectedItem
                is ForgeLoaderVersion;
    }
}
