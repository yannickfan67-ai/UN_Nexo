using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;
using UN.Nexo.Downloads;
namespace UN.Nexo.Desktop.ViewModels;

public partial class DownloadJobItem(DownloadJob value) : ObservableObject
{
    public DownloadJob Value { get; private set; } = value;
    public string Name => Value.Name;
    public string VersionId => Value.VersionId;
    public string StateLabel => Value.State switch { "Queued" => "等待中", "Downloading" => "下載中", "Paused" => "已暫停", "Failed" => "失敗", "Complete" => "完成", _ => Value.State };
    public string Message => Value.Message ?? "";
    public void Update(string state, string? message = null)
    {
        Value = Value with { State = state, Message = message, UpdatedAt = DateTimeOffset.UtcNow };
        OnPropertyChanged(nameof(StateLabel)); OnPropertyChanged(nameof(Message));
    }
}

public partial class DownloadCenterViewModel : ObservableObject
{
    private readonly NexoPathService _paths = new();
    private readonly DownloadSourceService _sources = new();
    private readonly MinecraftVersionManifestService _manifest;
    private readonly MinecraftVanillaInstallService _installer;
    private readonly InstanceStoreService _instances;
    private readonly DownloadWorkspace _workspace;
    private readonly LauncherSettingsService _settings;
    private readonly HttpClient _http;
    private readonly HttpClient _metadataHttp;
    private IReadOnlyList<MinecraftVersionInfo> _catalog = [];
    private CancellationTokenSource? _active;
    private Task? _worker;
    private bool _paused;
    private long _lastProgress;
    public ObservableCollection<MinecraftVersionInfo> Versions { get; } = [];
    public ObservableCollection<GameInstance> Instances { get; } = [];
    public ObservableCollection<DownloadJobItem> Jobs { get; } = [];
    public IReadOnlyList<string> Channels { get; } = ["正式版", "快照", "歷史版本", "全部"];
    public IReadOnlyList<string> Sources { get; } = ["官方", "BMCLAPI（官方備援）"];
    public string VersionLabel => "v" + (typeof(DownloadCenterViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev");
    public string DataRoot => _paths.GetDataRoot();
    [ObservableProperty] private MinecraftVersionInfo? _selectedVersion;
    [ObservableProperty] private GameInstance? _selectedInstance;
    [ObservableProperty] private DownloadJobItem? _selectedJob;
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private string _channel = "正式版";
    [ObservableProperty] private string _source = "官方";
    [ObservableProperty] private string _instanceName = "";
    [ObservableProperty] private string _renameName = "";
    [ObservableProperty] private string _status = "準備下載工作區…";
    [ObservableProperty] private string _catalogStatus = "載入版本目錄…";
    [ObservableProperty] private string _latestRelease = "—";
    [ObservableProperty] private string _latestSnapshot = "—";
    [ObservableProperty] private string _activeName = "尚無下載任務";
    [ObservableProperty] private string _transfer = "選擇版本，加入佇列即可開始。";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private string _reportPath = "";
    [ObservableProperty] private int _selectedTab;
    public DownloadCenterViewModel()
    {
        _http = CreateClient(TimeSpan.FromMinutes(10)); _metadataHttp = CreateClient(TimeSpan.FromSeconds(20));
        _manifest = new(_metadataHttp, _sources); _installer = new(_http, _paths, _sources);
        _instances = new(_paths); _workspace = new(_paths); _settings = new(_paths);
    }
    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("UN_Nexo/0.9.0-download-only"); return client;
    }
    public async Task InitializeAsync()
    {
        _paths.EnsureDirectories();
        var settings = await _settings.LoadAsync(); Source = settings.DownloadSource == "bmclapi" ? Sources[1] : Sources[0];
        foreach (var job in await _workspace.LoadJobsAsync()) Jobs.Add(new(job));
        _paused = Jobs.Any(x => x.Value.State == "Paused");
        await ReloadInstancesAsync(); await RefreshCatalogAsync();
        Status = _paused ? "上次未完成的任務已保留；按「繼續佇列」恢復。" : "下載限定版 · 可下載、校驗及修復遊戲檔案。";
    }
    partial void OnQueryChanged(string value) => FilterVersions();
    partial void OnChannelChanged(string value) => FilterVersions();
    partial void OnSelectedInstanceChanged(GameInstance? value) => RenameName = value?.Name ?? "";
    partial void OnSourceChanged(string value) => _sources.SetSource(value == Sources[1] ? "bmclapi" : "official");
    private void FilterVersions()
    {
        var selected = SelectedVersion?.Id;
        var filtered = _catalog.Where(v => (Channel switch { "正式版" => v.Type == "release", "快照" => v.Type == "snapshot", "歷史版本" => v.Type is "old_alpha" or "old_beta", _ => true })
            && (string.IsNullOrWhiteSpace(Query) || v.Id.Contains(Query.Trim(), StringComparison.OrdinalIgnoreCase))).ToArray();
        Versions.Clear(); foreach (var item in filtered) Versions.Add(item);
        SelectedVersion = Versions.FirstOrDefault(v => v.Id == selected) ?? Versions.FirstOrDefault();
    }
    [RelayCommand] private async Task RefreshCatalogAsync()
    {
        if (IsRefreshing) return; IsRefreshing = true;
        try
        {
            var catalog = await _manifest.GetCatalogAsync(); ApplyCatalog(catalog); await _workspace.SaveCatalogAsync(catalog);
            CatalogStatus = $"官方目錄 · {catalog.Versions.Count} 個版本 · {DateTimeOffset.Now:MM/dd HH:mm}";
        }
        catch (Exception ex)
        {
            try
            {
                var cached = await _workspace.LoadCatalogAsync();
                if (cached is not null) { ApplyCatalog(cached.Catalog); CatalogStatus = $"離線目錄 · 快取於 {cached.FetchedAt.ToLocalTime():MM/dd HH:mm}；下載仍需要網路。"; }
                else CatalogStatus = "無法取得版本目錄：" + ex.Message;
            }
            catch (Exception cacheError) { CatalogStatus = "目錄載入失敗：" + cacheError.Message; }
        }
        finally { IsRefreshing = false; }
    }
    private void ApplyCatalog(MinecraftVersionCatalog c) { _catalog = c.Versions; LatestRelease = c.Latest.LatestRelease; LatestSnapshot = c.Latest.LatestSnapshot; FilterVersions(); }
    [RelayCommand] private async Task SaveSettingsAsync()
    {
        try { await _settings.SaveAsync(new LauncherSettings { DownloadSource = _sources.SourceId }); Status = "下載來源已儲存。"; } catch (Exception ex) { Status = ex.Message; }
    }
    [RelayCommand] private async Task QueueVersionAsync()
    {
        if (SelectedVersion is null) { Status = "先選擇版本。"; return; }
        try
        {
            var version = SelectedVersion;
            var name = string.IsNullOrWhiteSpace(InstanceName) ? $"Minecraft {version.Id}" : InstanceName.Trim();
            if (name.Length > 128) throw new InvalidOperationException("版本名稱請限制在 128 字元內。");
            var instance = Instances.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && x.VersionId == version.Id && x.Loader == "vanilla")
                ?? await _instances.CreateAsync(name, version.Id);
            await ReloadInstancesAsync(instance.Id); await EnqueueAsync(instance);
        }
        catch (Exception ex) { Status = ex.Message; }
    }
    [RelayCommand] private async Task RepairInstanceAsync()
    {
        if (SelectedInstance is null) { Status = "先選擇本機版本。"; return; }
        if (SelectedInstance.Loader != "vanilla") { Status = "此版只修復 Vanilla 檔案；既有模組實例與存檔會保留。"; return; }
        try { await EnqueueAsync(SelectedInstance); } catch (Exception ex) { Status = ex.Message; }
    }
    private async Task EnqueueAsync(GameInstance instance)
    {
        if (Jobs.Any(j => j.Value.InstanceId == instance.Id && j.Value.State is "Queued" or "Downloading")) { Status = "這個版本已在佇列中。"; return; }
        while (Jobs.Count >= DownloadWorkspace.MaxJobs)
        {
            var oldest = Jobs.FirstOrDefault(j => j.Value.State == "Complete");
            if (oldest is null) throw new InvalidOperationException("佇列已滿；請先完成現有任務。"); Jobs.Remove(oldest);
        }
        var job = new DownloadJobItem(new(Guid.NewGuid().ToString("N"), instance.Id, instance.Name, instance.VersionId, _paused ? "Paused" : "Queued", DateTimeOffset.UtcNow));
        Jobs.Add(job); SelectedJob = job; SelectedTab = 2; await PersistAsync(); Status = instance.Name + " 已加入佇列。"; StartWorker();
    }
    private void StartWorker() { if (!_paused && (_worker is null || _worker.IsCompleted)) _worker = ProcessQueueAsync(); }
    private async Task ProcessQueueAsync()
    {
        IsBusy = true;
        try
        {
            while (!_paused)
            {
                var job = Jobs.FirstOrDefault(x => x.Value.State == "Queued"); if (job is null) break;
                using var cancellation = new CancellationTokenSource(); _active = cancellation;
                try
                {
                    var instance = (await _instances.GetAllAsync()).FirstOrDefault(x => x.Id == job.Value.InstanceId) ?? throw new InvalidOperationException("此任務的本機版本已不存在。");
                    var version = _catalog.FirstOrDefault(x => x.Id == job.VersionId) ?? throw new InvalidOperationException("請更新目錄後重試此版本。");
                    job.Update("Downloading"); ActiveName = instance.Name; Progress = 0; await PersistAsync();
                    var reporter = new Progress<InstallProgress>(p =>
                    {
                        var now = Environment.TickCount64; if (now - _lastProgress < 120 && p.Completed < p.Total) return; _lastProgress = now;
                        Progress = p.Percent;
                        Transfer = $"{p.Stage} · {p.Completed}/{p.Total}\n{p.CurrentItem}\n{FormatBytes(p.BytesDownloaded)} / {(p.TotalBytes is {} total ? FormatBytes(total) : "—")} · {FormatBytes((long)p.BytesPerSecond)}/s · {p.Source}";
                    });
                    await _installer.InstallAsync(instance, version, reporter, cancellation.Token);
                    job.Update("Complete", "遊戲檔案下載與 SHA-1 校驗完成。"); Progress = 100; Status = instance.Name + " 已完成。此版本不提供遊戲啟動功能。";
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { job.Update("Paused", "已保留通過校驗的完整檔案；繼續時只補下載缺失或損壞檔案。"); }
                catch (Exception ex) { job.Update("Failed", ex.Message.Length > 1024 ? ex.Message[..1024] : ex.Message); Status = "下載失敗，可選擇任務後重試：" + ex.Message; }
                finally { _active = null; await PersistAsync(); }
            }
        }
        catch (Exception ex) { Status = "無法儲存佇列：" + ex.Message; _paused = true; }
        finally { IsBusy = false; }
    }
    [RelayCommand] private async Task PauseQueueAsync()
    {
        _paused = true; _active?.Cancel(); foreach (var job in Jobs.Where(x => x.Value.State == "Queued")) job.Update("Paused", "等待繼續佇列。");
        try { await PersistAsync(); Status = "已暫停佇列。"; } catch (Exception ex) { Status = ex.Message; }
    }
    [RelayCommand] private async Task ResumeQueueAsync()
    {
        if (IsBusy) { Status = "請等待目前任務完成暫停。"; return; }
        foreach (var job in Jobs.Where(x => x.Value.State == "Paused")) job.Update("Queued"); _paused = false;
        try { await PersistAsync(); StartWorker(); } catch (Exception ex) { Status = ex.Message; }
    }
    [RelayCommand] private async Task RetryJobAsync()
    {
        if (SelectedJob is not {} job || job.Value.State is "Downloading" or "Queued") return;
        if (Jobs.Any(x => x != job && x.Value.InstanceId == job.Value.InstanceId && x.Value.State is "Queued" or "Downloading")) { Status = "此版本已有下載任務。"; return; }
        job.Update(_paused ? "Paused" : "Queued"); try { await PersistAsync(); StartWorker(); } catch (Exception ex) { Status = ex.Message; }
    }
    [RelayCommand] private async Task RenameInstanceAsync()
    {
        if (SelectedInstance is null) return;
        try
        {
            if (string.IsNullOrWhiteSpace(RenameName) || RenameName.Length > 128) throw new InvalidOperationException("請輸入 1–128 字元的名稱。");
            var item = await new InstanceLifecycleService(_paths).RenameAsync(SelectedInstance, RenameName); await ReloadInstancesAsync(item.Id); Status = "名稱已更新。";
        }
        catch (Exception ex) { Status = ex.Message; }
    }
    [RelayCommand] private void OpenInstanceFolder()
    {
        if (SelectedInstance is null) return;
        try { var path = _paths.GetInstanceGameDirectory(SelectedInstance.Id); Directory.CreateDirectory(path); OpenFolder(path); } catch (Exception ex) { Status = ex.Message; }
    }
    [RelayCommand] private void OpenDataFolder() { try { OpenFolder(_paths.EnsureDataRootPhysical()); } catch (Exception ex) { Status = ex.Message; } }
    private static void OpenFolder(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    [RelayCommand] private async Task ExportReportAsync()
    {
        try { ReportPath = await _workspace.ExportReportAsync(Jobs.Select(x => x.Value)); Status = "已匯出下載報告：" + ReportPath; } catch (Exception ex) { Status = ex.Message; }
    }
    private Task PersistAsync() => _workspace.SaveJobsAsync(Jobs.Select(x => x.Value));
    private async Task ReloadInstancesAsync(string? selectId = null)
    {
        var previous = selectId ?? SelectedInstance?.Id; var instances = await _instances.GetAllAsync();
        Instances.Clear(); foreach (var item in instances) Instances.Add(item); SelectedInstance = Instances.FirstOrDefault(x => x.Id == previous) ?? Instances.FirstOrDefault();
    }
    private static string FormatBytes(long value) => value >= 1048576 ? $"{value / 1048576d:0.0} MiB" : $"{value / 1024d:0.0} KiB";
}
