using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Launching;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Desktop.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly JavaDiscoveryService _javaDiscovery;
    private readonly NexoPathService _paths;
    private readonly MinecraftVersionManifestService _manifest;
    private readonly InstanceStoreService _instances;
    private readonly MinecraftVanillaInstallService _installer;
    private readonly FabricInstallService _fabricInstaller;
    private readonly AccountStoreService _accounts;
    private readonly MicrosoftMinecraftAuthService _microsoftAuth;
    private readonly RestrictedRegionService _restrictedRegions;
    private readonly LauncherSettingsService _settings;
    private readonly DownloadSourceService _downloadSources;
    private readonly MinecraftLaunchPlanBuilder _launchBuilder;
    private readonly MinecraftRuntimeInspector _runtimeInspector;
    private readonly JavaRuntimeProvisionService _runtimeProvisioner;
    private readonly InstanceOperationCoordinator _operations;
    private readonly MinecraftProcessService _gameProcess = new();
    private CancellationTokenSource? _gameCancellation;
    private readonly Queue<string> _gameLogLines = new();
    private bool _settingsLoadFailed;


    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isInstallBusy;
    [ObservableProperty] private bool isHomeVisible = true;
    [ObservableProperty] private bool isInstancesVisible;
    [ObservableProperty] private bool isAccountsVisible;
    [ObservableProperty] private bool isSettingsVisible;
    [ObservableProperty] private bool hasSelectedInstance;
    [ObservableProperty] private bool hasSelectedVersion;
    [ObservableProperty] private bool hasSelectedAccount;
    [ObservableProperty] private string launcherStatus = "Ready";
    [ObservableProperty] private string minecraftDirectory = "Detecting…";
    [ObservableProperty] private string instanceRoot = "Detecting…";
    [ObservableProperty] private string javaSummary = "Not scanned";
    [ObservableProperty] private string javaDetail = "Run environment scan to detect Java.";
    [ObservableProperty] private string latestRelease = "Unknown";
    [ObservableProperty] private string latestSnapshot = "Unknown";
    [ObservableProperty] private string instanceSummary = "0 instances";
    [ObservableProperty] private string catalogStatus = "Loading version catalog…";
    [ObservableProperty] private string newInstanceName = "New Minecraft";
    [ObservableProperty] private MinecraftVersionInfo? selectedVersion;
    [ObservableProperty] private GameInstance? selectedInstance;
    [ObservableProperty] private LauncherAccount? selectedAccount;
    [ObservableProperty] private double installProgressValue;
    [ObservableProperty] private string installProgressText = "Not installed";
    [ObservableProperty] private string offlineUserName = string.Empty;
    [ObservableProperty] private string accountSummary = "No account selected";
    [ObservableProperty] private string selectedDownloadSource = "Official";
    [ObservableProperty] private string downloadSourceStatus = "Official Mojang/Minecraft services";
    [ObservableProperty] private string microsoftAuthStatus = "UN_Nexo is configured as a Microsoft public client. Sign in opens your system browser; refresh credentials stay in the operating system's secure credential store.";
    [ObservableProperty] private bool isAccountAuthBusy;

    [ObservableProperty] private bool isGameRunning;
    [ObservableProperty] private string gameStatus = "Select an instance and a profile.";
    [ObservableProperty] private string gameLog = string.Empty;

    public bool CanPlay => !IsBusy && !IsInstallBusy && !IsAccountAuthBusy && !IsGameRunning
        && SelectedInstance is not null
        && SelectedAccount is not null;

    private void UpdatePlayAvailability()
    {
        OnPropertyChanged(nameof(CanPlay));
        PlayCommand.NotifyCanExecuteChanged();
        StopGameCommand.NotifyCanExecuteChanged();
        if (IsGameRunning)
            return;

        if (IsInstallBusy)
            GameStatus = "Wait for file preparation to finish.";
        else if (IsAccountAuthBusy)
            GameStatus = "Microsoft account operation in progress…";
        else if (IsBusy)
            GameStatus = "Scanning environment…";
        else if (SelectedInstance is null)
            GameStatus = "Create or select an instance in Instances.";
        else if (SelectedAccount is null)
            GameStatus = "Select a profile in Accounts.";
        else if (!File.Exists(Path.Combine(_paths.GetInstanceDirectory(SelectedInstance.Id), "install-state.json")))
            GameStatus = "Ready · required Minecraft files will download automatically.";
        else if (JavaInstallations.Count == 0)
            GameStatus = "Ready · required Java will download automatically.";
        else if (SelectedAccount.IsMicrosoft)
            GameStatus = SelectedAccount.EntitlementVerifiedAt is null
                ? "Ready · Microsoft sign-in is required before this profile can play."
                : "Ready · Microsoft credentials will refresh securely when you press Play.";
        else
            GameStatus = "Offline profiles cannot launch directly · use a verified Microsoft profile.";
    }

    partial void OnIsBusyChanged(bool value) => UpdatePlayAvailability();
    partial void OnIsInstallBusyChanged(bool value) => UpdatePlayAvailability();
    partial void OnIsAccountAuthBusyChanged(bool value) => UpdatePlayAvailability();
    partial void OnIsGameRunningChanged(bool value) => UpdatePlayAvailability();


    public ObservableCollection<JavaInstallation> JavaInstallations { get; } = [];
    public ObservableCollection<MinecraftVersionInfo> AvailableVersions { get; } = [];
    public ObservableCollection<GameInstance> Instances { get; } = [];
    public ObservableCollection<LauncherAccount> Accounts { get; } = [];
    public ObservableCollection<string> DownloadSources { get; } = ["Official", "BMCLAPI"];

    public MainWindowViewModel(
        JavaDiscoveryService javaDiscovery,
        NexoPathService paths,
        MinecraftVersionManifestService manifest,
        InstanceStoreService instances,
        MinecraftVanillaInstallService installer,
        FabricInstallService fabricInstaller,
        AccountStoreService accounts,
        MicrosoftMinecraftAuthService microsoftAuth,
        RestrictedRegionService restrictedRegions,
        LauncherSettingsService settings,
        DownloadSourceService downloadSources)
    {
        _launchBuilder = new MinecraftLaunchPlanBuilder(paths);
        _runtimeInspector = new MinecraftRuntimeInspector(paths);
        _runtimeProvisioner = new JavaRuntimeProvisionService(paths);
        _operations = new InstanceOperationCoordinator(paths);
        _javaDiscovery = javaDiscovery;
        _paths = paths;
        _manifest = manifest;
        _instances = instances;
        _installer = installer;
        _fabricInstaller = fabricInstaller;
        _accounts = accounts;
        _microsoftAuth = microsoftAuth;
        _restrictedRegions = restrictedRegions;
        _settings = settings;
        _downloadSources = downloadSources;
        JavaInstallations.CollectionChanged += (_, _) => UpdatePlayAvailability();
    }

    public async Task InitializeAsync()
    {
        try
        {
            var settings = await _settings.LoadAsync();
            _settingsLoadFailed = false;
            _downloadSources.SetSource(settings.DownloadSource);
            SelectedDownloadSource = _downloadSources.SourceId == "bmclapi" ? "BMCLAPI" : "Official";
            UpdateDownloadSourceStatus();
        }
        catch (Exception ex) when (
            ex is InvalidDataException
            or IOException
            or UnauthorizedAccessException)
        {
            _settingsLoadFailed = true;
            SelectedDownloadSource = _downloadSources.SourceId == "bmclapi" ? "BMCLAPI" : "Official";
            DownloadSourceStatus =
                $"settings.json could not be loaded: {ex.Message} The existing file was preserved and download-source changes are disabled until it is recovered.";
            LauncherStatus = "Launcher settings need recovery";
        }

        await ReloadAccountsAsync();
        await RefreshEnvironmentAsync();
    }

    partial void OnSelectedVersionChanged(MinecraftVersionInfo? value)
    {
        HasSelectedVersion = value is not null;
        if (value is not null
            && (NewInstanceName == "New Minecraft" || NewInstanceName.StartsWith("Minecraft ", StringComparison.Ordinal)))
            NewInstanceName = $"Minecraft {value.Id}";
    }

    partial void OnSelectedInstanceChanged(GameInstance? value)
    {
        HasSelectedInstance = value is not null;
        UpdatePlayAvailability();
        if (value is null)
        {
            InstallProgressValue = 0;
            InstallProgressText = "Not installed";
            return;
        }

        var prepared = File.Exists(Path.Combine(_paths.GetInstanceDirectory(value.Id), "install-state.json"));
        InstallProgressValue = prepared ? 100 : 0;
        InstallProgressText = prepared ? "Vanilla files prepared" : "Not installed";
    }

    partial void OnSelectedAccountChanged(LauncherAccount? value)
    {
        HasSelectedAccount = value is not null;
        UpdatePlayAvailability();
        AccountSummary = value is null
            ? "No account selected"
            : value.IsOffline
                ? $"{value.DisplayName} · Offline profile"
                : $"{value.DisplayName} · Microsoft";
    }

    [RelayCommand]
    private void ShowHome() => ShowPage("home");

    [RelayCommand]
    private void ShowInstances() => ShowPage("instances");

    [RelayCommand]
    private void ShowAccounts() => ShowPage("accounts");

    [RelayCommand]
    private void ShowSettings() => ShowPage("settings");

    private void ShowPage(string page)
    {
        IsHomeVisible = page == "home";
        IsInstancesVisible = page == "instances";
        IsAccountsVisible = page == "accounts";
        IsSettingsVisible = page == "settings";
    }

    [RelayCommand]
    private async Task RefreshEnvironmentAsync()
    {
        if (IsBusy || IsGameRunning)
            return;

        IsBusy = true;
        LauncherStatus = $"Scanning environment · {_downloadSources.DisplayName} source…";
        MinecraftDirectory = _paths.GetMinecraftDirectory();
        InstanceRoot = _paths.GetInstancesRoot();
        _paths.EnsureDirectories();

        var previousInstanceId = SelectedInstance?.Id;
        var previousVersionId = SelectedVersion?.Id;

        try
        {
            var javaTask = _javaDiscovery.DiscoverAsync();
            var catalogTask = TryGetCatalogAsync();
            var instancesTask = _instances.GetAllAsync();

            var java = await javaTask;
            var catalog = await catalogTask;
            var instances = await instancesTask;

            JavaInstallations.Clear();
            foreach (var installation in java)
                JavaInstallations.Add(installation);

            Instances.Clear();
            foreach (var instance in instances)
                Instances.Add(instance);

            AvailableVersions.Clear();
            if (catalog is not null)
            {
                var releases = catalog.Versions.Where(v => v.Type.Equals("release", StringComparison.OrdinalIgnoreCase)).ToArray();
                var recentSnapshots = catalog.Versions
                    .Where(v => v.Type.Equals("snapshot", StringComparison.OrdinalIgnoreCase))
                    .Take(40)
                    .ToArray();

                foreach (var version in releases.Concat(recentSnapshots))
                    AvailableVersions.Add(version);

                LatestRelease = catalog.Latest.LatestRelease;
                LatestSnapshot = catalog.Latest.LatestSnapshot;
                CatalogStatus = $"{releases.Length} releases · {recentSnapshots.Length} recent snapshots · {_downloadSources.DisplayName}";

                SelectedVersion = AvailableVersions.FirstOrDefault(v => v.Id == previousVersionId)
                    ?? AvailableVersions.FirstOrDefault(v => v.Id == catalog.Latest.LatestRelease)
                    ?? AvailableVersions.FirstOrDefault();
            }
            else
            {
                LatestRelease = "Offline";
                LatestSnapshot = "Offline";
                CatalogStatus = $"Version catalog unavailable · {_downloadSources.DisplayName} and fallback failed";
                SelectedVersion = null;
            }

            SelectedInstance = Instances.FirstOrDefault(v => v.Id == previousInstanceId)
                ?? Instances.FirstOrDefault();

            JavaSummary = java.Count == 0 ? "No Java found" : $"{java.Count} Java installation{(java.Count == 1 ? string.Empty : "s")}";
            JavaDetail = java.Count == 0
                ? "No Java runtime detected. Nexo will download the version required by the selected instance when you press Play."
                : $"Detected: Java {java[0].Version} · {(java[0].Is64Bit ? "64-bit" : "architecture unknown")}\n{java[0].JavaPath}";

            InstanceSummary = $"{instances.Count} instance{(instances.Count == 1 ? string.Empty : "s")}";
            LauncherStatus = catalog is null ? "Ready · version service offline" : $"Ready · {_downloadSources.DisplayName}";
        }
        catch (Exception ex)
        {
            CatalogStatus = "Version catalog unavailable";
            LauncherStatus = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            UpdatePlayAvailability();
        }
    }

    [RelayCommand]
    private async Task CreateInstanceAsync()
    {
        if (IsBusy || IsGameRunning || SelectedVersion is null)
            return;

        var name = string.IsNullOrWhiteSpace(NewInstanceName) ? SelectedVersion.Id : NewInstanceName.Trim();
        IsBusy = true;
        LauncherStatus = $"Creating {name}…";
        try
        {
            var instance = await _instances.CreateAsync(name, SelectedVersion.Id);
            Instances.Add(instance);
            SelectedInstance = instance;
            InstanceSummary = $"{Instances.Count} instance{(Instances.Count == 1 ? string.Empty : "s")}";
            LauncherStatus = $"Created {instance.Name}";
        }
        catch (Exception ex)
        {
            LauncherStatus = $"Create failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task PrepareSelectedInstanceAsync()
    {
        if (IsInstallBusy || IsGameRunning || SelectedInstance is null)
            return;

        var targetInstance = SelectedInstance;
        var targetSource = _downloadSources.DisplayName;

        IsInstallBusy = true;
        InstallProgressValue = 0;
        InstallProgressText = $"Starting via {targetSource}…";
        LauncherStatus = $"Preparing {targetInstance.Name}…";

        var progress = new Progress<InstallProgress>(value =>
        {
            if (!IsSelectedInstance(targetInstance))
                return;

            InstallProgressValue = value.Percent;
            InstallProgressText = value.Total > 0
                ? $"{value.Stage} · {value.Completed}/{value.Total}{(string.IsNullOrWhiteSpace(value.CurrentItem) ? string.Empty : $" · {value.CurrentItem}")}"
                : value.Stage;
        });

        try
        {
            await PrepareInstanceFilesAsync(
                targetInstance,
                progress,
                CancellationToken.None);
            if (IsSelectedInstance(targetInstance))
            {
                InstallProgressValue = 100;
                InstallProgressText = targetInstance.Loader.Equals("fabric", StringComparison.OrdinalIgnoreCase)
                    ? $"Fabric files prepared · {targetSource}"
                    : $"Vanilla files prepared · {targetSource}";
            }
            LauncherStatus = $"{targetInstance.Name} is prepared";
        }
        catch (Exception ex)
        {
            if (IsSelectedInstance(targetInstance))
                InstallProgressText = "Install failed";
            LauncherStatus = $"Install failed for {targetInstance.Name}: {ex.Message}";
        }
        finally
        {
            IsInstallBusy = false;
            if (!IsSelectedInstance(targetInstance) && SelectedInstance is not null)
                RefreshSelectedInstanceInstallState();
        }
    }

    private async Task EnsureLaunchReadyAsync(
        GameInstance instance,
        CancellationToken cancellationToken)
    {
        var statePath = Path.Combine(
            _paths.GetInstanceDirectory(instance.Id),
            "install-state.json");
        if (!File.Exists(statePath))
        {
            IsInstallBusy = true;
            InstallProgressValue = 0;
            InstallProgressText = $"Auto preparing {instance.VersionId}…";
            try
            {
                var progress = new Progress<InstallProgress>(value =>
                {
                    if (IsSelectedInstance(instance))
                    {
                        InstallProgressValue = value.Percent;
                        InstallProgressText = value.Total > 0
                            ? $"{value.Stage} · {value.Completed}/{value.Total}"
                            : value.Stage;
                    }

                    GameStatus = value.Total > 0
                        ? $"Downloading {instance.VersionId} · {value.Stage} · {value.Completed}/{value.Total}"
                        : $"Downloading {instance.VersionId} · {value.Stage}";
                    LauncherStatus = GameStatus;
                });

                await PrepareInstanceFilesAsync(
                    instance,
                    progress,
                    cancellationToken);

                if (IsSelectedInstance(instance))
                {
                    InstallProgressValue = 100;
                    InstallProgressText = instance.Loader.Equals(
                        "fabric",
                        StringComparison.OrdinalIgnoreCase)
                        ? $"Fabric files prepared · {_downloadSources.DisplayName}"
                        : $"Vanilla files prepared · {_downloadSources.DisplayName}";
                }
            }
            finally
            {
                IsInstallBusy = false;
            }
        }

        var requiredJava = await _runtimeInspector
            .GetRequiredJavaMajorAsync(instance, cancellationToken) ?? 8;
        var matchingJava = JavaInstallations.FirstOrDefault(item =>
            item.Is64Bit
            && File.Exists(item.JavaPath)
            && MinecraftLaunchPlanBuilder.JavaMajor(item.Version) == requiredJava);
        if (matchingJava is null)
        {
            GameStatus = $"Java {requiredJava} is missing · downloading a managed runtime…";
            LauncherStatus = GameStatus;
            var progress = new Progress<string>(message =>
            {
                GameStatus = message;
                LauncherStatus = message;
            });
            var installed = await _runtimeProvisioner.EnsureJavaAsync(
                requiredJava,
                progress,
                cancellationToken);
            if (!JavaInstallations.Any(item =>
                    string.Equals(
                        item.JavaPath,
                        installed.JavaPath,
                        StringComparison.OrdinalIgnoreCase)))
                JavaInstallations.Add(installed);
            JavaSummary =
                $"{JavaInstallations.Count} Java installation{(JavaInstallations.Count == 1 ? string.Empty : "s")}";
            JavaDetail =
                $"Managed Java {installed.Version} · 64-bit\n{installed.JavaPath}";
        }

        GameStatus = $"Starting {instance.Name}…";
        LauncherStatus = GameStatus;
    }

    private async Task PrepareInstanceFilesAsync(
        GameInstance instance,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (instance.Loader.Equals("vanilla", StringComparison.OrdinalIgnoreCase))
        {
            var version = await ResolveCatalogVersionAsync(
                instance.VersionId,
                cancellationToken);
            await _installer.InstallAsync(
                instance,
                version,
                progress,
                cancellationToken);
            return;
        }

        if (instance.Loader.Equals("fabric", StringComparison.OrdinalIgnoreCase))
        {
            var baseVersionId = !string.IsNullOrWhiteSpace(instance.BaseVersionId)
                ? instance.BaseVersionId
                : await _fabricInstaller.GetBaseVersionIdAsync(
                    instance,
                    cancellationToken);
            if (string.IsNullOrWhiteSpace(baseVersionId))
                throw new InvalidDataException(
                    "Fabric instance does not declare its base Minecraft version.");

            var baseVersion = await ResolveCatalogVersionAsync(
                baseVersionId,
                cancellationToken);
            await _fabricInstaller.PrepareAsync(
                instance,
                baseVersion,
                progress,
                cancellationToken);
            return;
        }

        throw new NotSupportedException(
            $"Automatic preparation is not implemented for loader '{instance.Loader}'.");
    }

    private async Task<MinecraftVersionInfo> ResolveCatalogVersionAsync(
        string versionId,
        CancellationToken cancellationToken)
    {
        var version = AvailableVersions.FirstOrDefault(item =>
            item.Id.Equals(versionId, StringComparison.Ordinal));
        if (version is not null)
            return version;

        MinecraftVersionCatalog? catalog;
        try
        {
            catalog = await _manifest.GetCatalogAsync(cancellationToken);
        }
        catch (Exception ex) when (
            ex is HttpRequestException
            or InvalidDataException
            or IOException)
        {
            throw new InvalidOperationException(
                $"Minecraft {versionId} metadata is unavailable. Refresh the catalog and try again.",
                ex);
        }

        version = catalog.Versions.FirstOrDefault(item =>
            item.Id.Equals(versionId, StringComparison.Ordinal));
        return version
            ?? throw new InvalidOperationException(
                $"Minecraft {versionId} metadata is unavailable. Refresh the catalog and try again.");
    }

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private async Task PlayAsync()
    {
        if (!CanPlay || SelectedInstance is null || SelectedAccount is null)
            return;

        var instance = SelectedInstance;
        var account = SelectedAccount;
        var launchAccount = account;
        MinecraftLaunchCredentials? credentials = null;
        var usedRestrictedOfflineFallback = false;
        var cancellation = new CancellationTokenSource();
        _gameCancellation = cancellation;
        IsGameRunning = true;
        _gameLogLines.Clear();
        GameLog = string.Empty;
        GameStatus = $"Checking {instance.Name}…";
        LauncherStatus = GameStatus;

        try
        {
            if (account.IsOffline)
            {
                throw new MicrosoftAuthenticationRequiredException(
                    "Generic offline profiles are not launchable. Sign in with a Microsoft account. In CN/RU, a previously entitlement-verified Microsoft profile may use restricted-region offline fallback when the online service is temporarily unreachable.");
            }

            await EnsureLaunchReadyAsync(instance, cancellation.Token);

            GameStatus = $"Waiting for exclusive access to {instance.Name}…";
            LauncherStatus = GameStatus;
            await using var operationLease = await _operations.AcquireAsync(
                instance.Id,
                "play",
                cancellation.Token);

            if (account.IsMicrosoft)
            {
                GameStatus = $"Refreshing Microsoft session for {account.DisplayName}…";
                LauncherStatus = GameStatus;
                try
                {
                    var session = await _microsoftAuth.AcquireSessionAsync(
                        account,
                        cancellation.Token);
                    launchAccount = session.Account;
                    credentials = session.Credentials;
                    ReplaceAccountInList(account, session.Account);
                }
                catch (Exception ex) when (
                    IsTransientOnlineSessionFailure(ex, cancellation.Token))
                {
                    GameStatus = "Online Minecraft session unavailable · checking restricted-region fallback…";
                    LauncherStatus = GameStatus;
                    if (!await _restrictedRegions.CanUseOfflineFallbackAsync(
                            account,
                            cancellation.Token))
                        throw;

                    launchAccount = account with
                    {
                        Type = "offline",
                        AuthenticationId = null
                    };
                    credentials = null;
                    usedRestrictedOfflineFallback = true;
                    AppendGameLog(
                        "Using restricted-region offline fallback for a previously entitlement-verified Microsoft profile. Public IP and country are not persisted.");
                }
            }

            var plan = await _launchBuilder.BuildAsync(
                instance,
                launchAccount,
                JavaInstallations.ToArray(),
                credentials,
                cancellation.Token);
            GameStatus = usedRestrictedOfflineFallback
                ? $"Running {instance.Name} · verified restricted-region offline fallback · {account.DisplayName}"
                : $"Running {instance.Name} · Microsoft profile {launchAccount.DisplayName}";
            LauncherStatus = GameStatus;
            var result = await _gameProcess.RunAsync(
                plan, new Progress<string>(AppendGameLog), cancellation.Token);
            GameStatus = result.ExitCode == 0
                ? $"{instance.Name} exited normally."
                : $"{instance.Name} exited with code {result.ExitCode}.";
            AppendGameLog($"Full log: {result.LogPath}");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            GameStatus = $"Stopped {instance.Name}.";
        }
        catch (MicrosoftAuthenticationRequiredException ex)
        {
            GameStatus = $"Microsoft sign-in required: {ex.Message}";
            AppendGameLog(GameStatus);
        }
        catch (MinecraftApplicationNotAuthorizedException ex)
        {
            GameStatus = ex.Message;
            MicrosoftAuthStatus = ex.Message;
            AppendGameLog("Minecraft Services rejected the UN_Nexo application registration.");
        }
        catch (Exception ex)
        {
            GameStatus = $"Launch failed: {ex.Message}";
            AppendGameLog(GameStatus);
        }
        finally
        {
            _gameCancellation = null;
            IsGameRunning = false;
            LauncherStatus = GameStatus;
            cancellation.Dispose();
        }
    }

    private static bool IsTransientOnlineSessionFailure(
        Exception exception,
        CancellationToken callerCancellation)
    {
        if (exception is OperationCanceledException)
            return !callerCancellation.IsCancellationRequested;
        if (exception is TimeoutException)
            return true;
        if (exception is not HttpRequestException http)
            return false;
        if (http.StatusCode is null)
            return true;

        var status = (int)http.StatusCode.Value;
        return status is 408 or 429 || status >= 500;
    }

    [RelayCommand(CanExecute = nameof(IsGameRunning))]
    private void StopGame() => _gameCancellation?.Cancel();

    private void AppendGameLog(string line)
    {
        _gameLogLines.Enqueue(line);
        while (_gameLogLines.Count > 200)
            _gameLogLines.Dequeue();
        GameLog = string.Join(Environment.NewLine, _gameLogLines);
    }

    [RelayCommand]
    private async Task CreateOfflineAccountAsync()
    {
        try
        {
            var account = await _accounts.CreateOfflineAsync(OfflineUserName);
            var existing = Accounts.FirstOrDefault(x => x.Id == account.Id);
            if (existing is null)
                Accounts.Add(account);
            SelectedAccount = existing ?? account;
            OfflineUserName = string.Empty;
            LauncherStatus = $"Offline profile {account.DisplayName} is ready";
        }
        catch (Exception ex)
        {
            LauncherStatus = $"Offline profile: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SignInMicrosoftAsync()
    {
        if (IsBusy || IsInstallBusy || IsAccountAuthBusy || IsGameRunning)
            return;

        IsAccountAuthBusy = true;
        MicrosoftAuthStatus = "Opening your system browser for Microsoft sign-in…";
        LauncherStatus = "Signing in with Microsoft…";
        try
        {
            var session = await _microsoftAuth.SignInAsync();
            var existing = Accounts.FirstOrDefault(item => item.Id == session.Account.Id);
            if (existing is null)
                Accounts.Add(session.Account);
            else
                ReplaceAccountInList(existing, session.Account);

            SelectedAccount = session.Account;
            MicrosoftAuthStatus =
                $"Signed in as {session.Account.DisplayName}. Refresh credentials are stored by the operating system, not in accounts.json.";
            LauncherStatus = $"Microsoft profile {session.Account.DisplayName} is ready";
        }
        catch (MinecraftApplicationNotAuthorizedException ex)
        {
            MicrosoftAuthStatus = ex.Message;
            LauncherStatus = "Minecraft Services has not authorized the UN_Nexo Client ID yet";
        }
        catch (OperationCanceledException)
        {
            MicrosoftAuthStatus = "Microsoft sign-in was cancelled.";
            LauncherStatus = "Microsoft sign-in cancelled";
        }
        catch (Exception ex)
        {
            MicrosoftAuthStatus = $"Microsoft sign-in failed: {ex.Message}";
            LauncherStatus = "Microsoft sign-in failed";
        }
        finally
        {
            IsAccountAuthBusy = false;
        }
    }

    [RelayCommand]
    private async Task SignOutSelectedMicrosoftAsync()
    {
        if (IsBusy || IsInstallBusy || IsAccountAuthBusy || IsGameRunning
            || SelectedAccount is not { IsMicrosoft: true } account)
            return;

        IsAccountAuthBusy = true;
        LauncherStatus = $"Signing out {account.DisplayName}…";
        try
        {
            await _microsoftAuth.SignOutAsync(account);
            Accounts.Remove(account);
            SelectedAccount = Accounts.FirstOrDefault();
            MicrosoftAuthStatus =
                $"Signed out {account.DisplayName}. Its cached Microsoft refresh credentials were removed from this device.";
            LauncherStatus = $"Signed out {account.DisplayName}";
        }
        catch (Exception ex)
        {
            MicrosoftAuthStatus = $"Could not sign out: {ex.Message}";
            LauncherStatus = "Microsoft sign-out failed";
        }
        finally
        {
            IsAccountAuthBusy = false;
        }
    }

    [RelayCommand]
    private void ExplainMicrosoftSignIn()
    {
        MicrosoftAuthStatus =
            $"UN_Nexo uses its own public Client ID {MsalMicrosoftAccessTokenProvider.ClientId}, the system browser, Xbox Live/XSTS and Minecraft Services. No client secret is embedded and another launcher's identity is never reused.";
    }

    private void ReplaceAccountInList(LauncherAccount previous, LauncherAccount current)
    {
        var index = Accounts.IndexOf(previous);
        if (index >= 0)
            Accounts[index] = current;
        else if (Accounts.All(item => item.Id != current.Id))
            Accounts.Add(current);

        if (SelectedAccount?.Id == previous.Id)
            SelectedAccount = current;
    }

    [RelayCommand]
    private async Task SaveDownloadSourceAsync()
    {
        if (_settingsLoadFailed)
        {
            LauncherStatus =
                "Download source was not saved because settings.json could not be loaded. Recover or explicitly reset that file first.";
            return;
        }

        var id = SelectedDownloadSource.Equals("BMCLAPI", StringComparison.OrdinalIgnoreCase)
            ? "bmclapi"
            : "official";
        _downloadSources.SetSource(id);
        await _settings.SaveAsync(new LauncherSettings(id));
        UpdateDownloadSourceStatus();
        LauncherStatus = $"Download source changed to {_downloadSources.DisplayName}";
        await RefreshEnvironmentAsync();
    }

    private async Task ReloadAccountsAsync()
    {
        var previousId = SelectedAccount?.Id;
        try
        {
            var accounts = await _accounts.GetAllAsync();
            Accounts.Clear();
            foreach (var account in accounts)
                Accounts.Add(account);
            SelectedAccount = Accounts.FirstOrDefault(x => x.Id == previousId)
                ?? Accounts.FirstOrDefault();
        }
        catch (Exception ex) when (
            ex is InvalidDataException
            or IOException
            or UnauthorizedAccessException)
        {
            MicrosoftAuthStatus =
                $"Saved accounts could not be loaded: {ex.Message} The existing accounts.json was preserved.";
            LauncherStatus = "Account store needs recovery";
        }
    }

    private bool IsSelectedInstance(GameInstance instance)
        => SelectedInstance?.Id == instance.Id;

    private void RefreshSelectedInstanceInstallState()
    {
        if (SelectedInstance is null)
        {
            InstallProgressValue = 0;
            InstallProgressText = "Not installed";
            return;
        }

        var prepared = File.Exists(Path.Combine(_paths.GetInstanceDirectory(SelectedInstance.Id), "install-state.json"));
        InstallProgressValue = prepared ? 100 : 0;
        InstallProgressText = prepared ? "Vanilla files prepared" : "Not installed";
    }

    private void UpdateDownloadSourceStatus()
    {
        DownloadSourceStatus = _downloadSources.SourceId == "bmclapi"
            ? "BMCLAPI mirror first · automatic fallback to official Mojang/Minecraft URLs"
            : "Official Mojang/Minecraft services only";
    }

    private async Task<MinecraftVersionCatalog?> TryGetCatalogAsync()
    {
        try
        {
            return await _manifest.GetCatalogAsync();
        }
        catch
        {
            return null;
        }
    }
}
