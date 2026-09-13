using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UN.Nexo.Core.Launching;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;
using UN.Nexo.Desktop.Diagnostics;

namespace UN.Nexo.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    [ObservableProperty] private bool isGameStarting;
    [ObservableProperty] private bool launchDebugEnabled = true;
    [ObservableProperty] private string launchDebugSummary = "Debug trace enabled · launch phases and Java process health will be recorded.";
    [ObservableProperty] private string lastLaunchTracePath = string.Empty;
    [ObservableProperty] private bool hasLaunchDiagnostic;
    [ObservableProperty] private string launchDiagnosticTitle = string.Empty;
    [ObservableProperty] private string launchDiagnosticSummary = string.Empty;
    [ObservableProperty] private string launchDiagnosticEvidence = string.Empty;
    [ObservableProperty] private string launchDiagnosticSuggestion = string.Empty;
    [ObservableProperty] private string lastMinecraftLogPath = string.Empty;
    [ObservableProperty] private string diagnosticPreviewText = string.Empty;
    [ObservableProperty] private string diagnosticExportStatus = string.Empty;
    [ObservableProperty] private string lastDiagnosticArchivePath = string.Empty;
    [ObservableProperty] private bool isDiagnosticExporting;

    public string LauncherVersion
    {
        get
        {
            var assembly = Assembly.GetEntryAssembly();
            var informational = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                var metadataIndex = informational.IndexOf('+');
                return "v" + (metadataIndex >= 0 ? informational[..metadataIndex] : informational);
            }

            return "v" + (assembly?.GetName().Version?.ToString() ?? "unknown");
        }
    }

    private readonly MinecraftCrashDiagnosisService _crashDiagnosis = new();
    private readonly DiagnosticBundleService _diagnosticBundles = new();
    private StreamWriter? _launchTraceWriter;
    private string? _lastTracedGameLine;
    private bool _debugProcessStarted;
    private string? _pendingTerminalGameStatus;
    private bool _restoringTerminalGameStatus;
    private CrashDiagnosis? _lastDiagnosis;
    private int? _lastDiagnosticExitCode;

    partial void OnLaunchDebugEnabledChanged(bool value)
    {
        LaunchDiagnostics.Enabled = value;
        LaunchDebugSummary = value
            ? "Debug trace enabled · launch phases and Java process health will be recorded."
            : "Debug trace disabled · basic game output is still kept.";
        WriteDebugTrace($"[debug] Detailed process diagnostics {(value ? "enabled" : "disabled")}");
    }

    partial void OnGameStatusChanged(string value)
    {
        if (_restoringTerminalGameStatus)
            return;

        if (IsTerminalStatus(value))
        {
            _pendingTerminalGameStatus = value;
        }
        else if (!IsGameRunning
                 && _pendingTerminalGameStatus is { Length: > 0 } terminal
                 && IsAvailabilityStatus(value))
        {
            _pendingTerminalGameStatus = null;
            _restoringTerminalGameStatus = true;
            try
            {
                GameStatus = terminal;
                LauncherStatus = terminal;
            }
            finally
            {
                _restoringTerminalGameStatus = false;
            }
            return;
        }

        if (IsGameRunning && _launchTraceWriter is null && IsLaunchBeginning(value))
            StartDebugTrace();

        if (_launchTraceWriter is not null)
            WriteDebugTrace($"[status] {value}");

        if (IsTerminalStatus(value))
        {
            IsGameStarting = false;
            LaunchDebugSummary = value;
            CloseDebugTrace();
            ApplyTerminalDiagnosis(value);
            return;
        }

        if (IsGameRunning && !_debugProcessStarted)
            IsGameStarting = true;
    }

    partial void OnGameLogChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        var index = value.LastIndexOf('\n');
        var line = (index >= 0 ? value[(index + 1)..] : value).TrimEnd('\r');
        if (string.IsNullOrWhiteSpace(line) || string.Equals(line, _lastTracedGameLine, StringComparison.Ordinal))
            return;

        _lastTracedGameLine = line;
        WriteDebugTrace(line);

        if (line.StartsWith("Started game process ", StringComparison.Ordinal))
        {
            _debugProcessStarted = true;
            IsGameStarting = false;
            LaunchDebugSummary = $"Java process started · {line["Started game process ".Length..]} · debug heartbeat active";
        }
        else if (line.StartsWith("[debug] PID ", StringComparison.Ordinal))
        {
            LaunchDebugSummary = line[8..];
        }
        else if (line.StartsWith("Log: ", StringComparison.Ordinal))
        {
            LastMinecraftLogPath = line[5..].Trim();
            LaunchDebugSummary = $"Minecraft process log · {LastMinecraftLogPath}";
        }
        else if (line.StartsWith("Full log: ", StringComparison.Ordinal))
        {
            LastMinecraftLogPath = line["Full log: ".Length..].Trim();
        }
    }

    [RelayCommand]
    private void OpenDebugFolder()
    {
        try
        {
            var directory = !string.IsNullOrWhiteSpace(LastMinecraftLogPath)
                ? Path.GetDirectoryName(LastMinecraftLogPath)
                : null;
            if (string.IsNullOrWhiteSpace(directory))
                directory = GetDebugDirectory();
            Directory.CreateDirectory(directory);
            var info = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "explorer.exe"
                    : OperatingSystem.IsMacOS() ? "open" : "xdg-open",
                UseShellExecute = false
            };
            info.ArgumentList.Add(directory);
            Process.Start(info);
        }
        catch (Exception ex)
        {
            LaunchDebugSummary = $"Could not open debug folder: {ex.Message}";
        }
    }

    [RelayCommand]
    private void DismissLaunchDiagnostic() => HasLaunchDiagnostic = false;

    [RelayCommand]
    private async Task ExportDiagnosticPackageAsync()
    {
        if (_lastDiagnosis is null || IsDiagnosticExporting)
            return;

        IsDiagnosticExporting = true;
        DiagnosticExportStatus = "Creating sanitized diagnostic ZIP…";
        try
        {
            var directory = Path.Combine(_paths.GetDataRoot(), "diagnostics");
            Directory.CreateDirectory(directory);
            var archivePath = Path.Combine(
                directory,
                $"UN_Nexo-diagnostic-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip");
            var result = await _diagnosticBundles.ExportAsync(
                _lastDiagnosis,
                _lastDiagnosticExitCode,
                GetDiagnosticSourceLogs(),
                archivePath,
                GetDiagnosticSecrets());
            LastDiagnosticArchivePath = result.ArchivePath;
            DiagnosticExportStatus = $"Exported sanitized ZIP · {result.IncludedLogs} log(s) · {result.ArchivePath}";
        }
        catch (Exception ex)
        {
            DiagnosticExportStatus = $"Diagnostic export failed: {ex.Message}";
        }
        finally
        {
            IsDiagnosticExporting = false;
        }
    }

    public string GetDiagnosticClipboardText()
    {
        if (_lastDiagnosis is null)
            return string.Empty;
        return _crashDiagnosis.Sanitize(
            _crashDiagnosis.BuildSummary(_lastDiagnosis),
            GetDiagnosticSecrets().ToArray());
    }

    public void MarkDiagnosticSummaryCopied()
        => DiagnosticExportStatus = "Sanitized diagnosis copied to clipboard.";

    public void MarkDiagnosticCopyFailed(string reason)
        => DiagnosticExportStatus = $"Could not copy diagnosis: {reason}";

    private void ApplyTerminalDiagnosis(string terminalStatus)
    {
        CrashDiagnosis diagnosis;
        var retainedOutput = string.Join(Environment.NewLine, _gameLogLines);
        int? exitCode = null;

        if (terminalStatus.StartsWith("Stopped ", StringComparison.OrdinalIgnoreCase))
        {
            diagnosis = _crashDiagnosis.Analyze(null, retainedOutput, wasStopped: true);
        }
        else if (terminalStatus.StartsWith("Launch failed:", StringComparison.OrdinalIgnoreCase))
        {
            var launchError = terminalStatus["Launch failed:".Length..].Trim();
            diagnosis = _crashDiagnosis.Analyze(null, retainedOutput, launchError);
        }
        else if (terminalStatus.Contains("exited normally", StringComparison.OrdinalIgnoreCase))
        {
            exitCode = 0;
            diagnosis = _crashDiagnosis.Analyze(0, retainedOutput);
        }
        else if (TryParseExitCodeFromStatus(terminalStatus, out var parsedExitCode))
        {
            exitCode = parsedExitCode;
            diagnosis = _crashDiagnosis.Analyze(parsedExitCode, retainedOutput);
        }
        else
        {
            diagnosis = _crashDiagnosis.Analyze(null, retainedOutput, terminalStatus);
        }

        _lastDiagnosis = diagnosis;
        _lastDiagnosticExitCode = exitCode;
        var secrets = GetDiagnosticSecrets().ToArray();
        LaunchDiagnosticTitle = _crashDiagnosis.Sanitize(diagnosis.Title, secrets);
        LaunchDiagnosticSummary = _crashDiagnosis.Sanitize(diagnosis.Summary, secrets);
        LaunchDiagnosticEvidence = _crashDiagnosis.Sanitize(diagnosis.Evidence, secrets);
        LaunchDiagnosticSuggestion = _crashDiagnosis.Sanitize(diagnosis.SuggestedAction, secrets);
        HasLaunchDiagnostic = true;
        LaunchDebugSummary = LaunchDiagnosticTitle;
        DiagnosticExportStatus = "Preview is sanitized before display and export.";
        _ = RefreshDiagnosticPreviewAsync(diagnosis, exitCode);
    }

    private async Task RefreshDiagnosticPreviewAsync(CrashDiagnosis diagnosis, int? exitCode)
    {
        try
        {
            var preview = await _diagnosticBundles.BuildPreviewAsync(
                diagnosis,
                exitCode,
                GetDiagnosticSourceLogs(),
                GetDiagnosticSecrets());
            var sources = preview.SourceLogs.Count == 0
                ? "No readable log file is currently available."
                : string.Join(Environment.NewLine, preview.SourceLogs.Select(path => "• " + path));
            DiagnosticPreviewText = $"Files considered for export:{Environment.NewLine}{sources}{Environment.NewLine}{Environment.NewLine}Sanitized preview:{Environment.NewLine}{preview.LogExcerpt}";
        }
        catch (Exception ex)
        {
            DiagnosticPreviewText = $"Could not create diagnostic preview: {ex.Message}";
        }
    }

    private IEnumerable<string?> GetDiagnosticSourceLogs()
    {
        yield return LastLaunchTracePath;
        yield return LastMinecraftLogPath;
        yield return LauncherStartupTrace.Path;

        if (SelectedInstance is null)
            yield break;

        var gameDirectory = _paths.GetInstanceGameDirectory(SelectedInstance.Id);
        foreach (var path in NewestMatchingFiles(Path.Combine(gameDirectory, "crash-reports"), "*.txt", 3))
            yield return path;
        foreach (var path in NewestMatchingFiles(gameDirectory, "hs_err_pid*.log", 2))
            yield return path;
    }

    private IEnumerable<string?> GetDiagnosticSecrets()
    {
        if (SelectedAccount is null)
            yield break;

        yield return SelectedAccount.Id;
        yield return SelectedAccount.Uuid;
    }

    private static IEnumerable<string> NewestMatchingFiles(string directory, string pattern, int count)
    {
        try
        {
            if (!Directory.Exists(directory))
                return [];
            return Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(count)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private void ClearLaunchDiagnostic()
    {
        HasLaunchDiagnostic = false;
        LaunchDiagnosticTitle = string.Empty;
        LaunchDiagnosticSummary = string.Empty;
        LaunchDiagnosticEvidence = string.Empty;
        LaunchDiagnosticSuggestion = string.Empty;
        LastMinecraftLogPath = string.Empty;
        DiagnosticPreviewText = string.Empty;
        DiagnosticExportStatus = string.Empty;
        LastDiagnosticArchivePath = string.Empty;
        _lastDiagnosis = null;
        _lastDiagnosticExitCode = null;
    }

    private static bool TryParseExitCodeFromStatus(string status, out int exitCode)
    {
        const string marker = "exited with code ";
        var index = status.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            exitCode = 0;
            return false;
        }

        var text = status[(index + marker.Length)..].Trim().TrimEnd('.');
        return int.TryParse(text, out exitCode);
    }

    private void StartDebugTrace()
    {
        CloseDebugTrace();
        ClearLaunchDiagnostic();
        var directory = GetDebugDirectory();
        Directory.CreateDirectory(directory);
        var instanceId = SelectedInstance?.Id ?? "no-instance";
        var safeInstanceId = string.Concat(instanceId.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        LastLaunchTracePath = Path.Combine(
            directory,
            $"launch-trace-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{safeInstanceId}.log");
        _launchTraceWriter = new StreamWriter(
            new FileStream(LastLaunchTracePath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true
        };
        _lastTracedGameLine = null;
        _debugProcessStarted = false;
        IsGameStarting = true;

        WriteDebugTrace($"[trace] UN_Nexo {LauncherVersion}");
        WriteDebugTrace($"[trace] Started {DateTimeOffset.Now:O}");
        WriteDebugTrace($"[trace] OS {RuntimeInformation.OSDescription.Trim()} · {RuntimeInformation.OSArchitecture} · process {RuntimeInformation.ProcessArchitecture}");
        WriteDebugTrace($"[trace] Instance {SelectedInstance?.Name ?? "none"} · Minecraft {SelectedInstance?.VersionId ?? "unknown"}");
        WriteDebugTrace($"[trace] Account {SelectedAccount?.DisplayName ?? "none"} · source {_downloadSources.DisplayName}");
        WriteDebugTrace($"[trace] Data root {_paths.GetDataRoot()}");
        LaunchDebugSummary = $"Tracing launch · {Path.GetFileName(LastLaunchTracePath)}";
    }

    private string GetDebugDirectory()
        => SelectedInstance is null
            ? Path.Combine(_paths.GetDataRoot(), "logs")
            : Path.Combine(_paths.GetInstanceDirectory(SelectedInstance.Id), "launcher-logs");

    private void WriteDebugTrace(string line)
    {
        if (_launchTraceWriter is null)
            return;

        try
        {
            _launchTraceWriter.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] {line}");
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
    }

    private void CloseDebugTrace()
    {
        var writer = _launchTraceWriter;
        _launchTraceWriter = null;
        if (writer is null)
            return;

        try
        {
            writer.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] [trace] Trace closed");
            writer.Dispose();
        }
        catch (IOException)
        {
            writer.Dispose();
        }
    }

    private static bool IsLaunchBeginning(string status)
        => status.StartsWith("Checking ", StringComparison.OrdinalIgnoreCase)
           || status.StartsWith("Preparing ", StringComparison.OrdinalIgnoreCase)
           || status.StartsWith("Downloading ", StringComparison.OrdinalIgnoreCase)
           || status.StartsWith("Starting ", StringComparison.OrdinalIgnoreCase)
           || status.StartsWith("Launching ", StringComparison.OrdinalIgnoreCase);

    private static bool IsTerminalStatus(string status)
        => status.Contains("exited", StringComparison.OrdinalIgnoreCase)
           || status.Contains("failed", StringComparison.OrdinalIgnoreCase)
           || status.StartsWith("Stopped ", StringComparison.OrdinalIgnoreCase);

    private static bool IsAvailabilityStatus(string status)
        => status.StartsWith("Ready", StringComparison.OrdinalIgnoreCase)
           || status.StartsWith("Wait for file preparation", StringComparison.OrdinalIgnoreCase)
           || status.StartsWith("Scanning environment", StringComparison.OrdinalIgnoreCase)
           || status.StartsWith("Create or select an instance", StringComparison.OrdinalIgnoreCase)
           || status.StartsWith("Select an offline profile", StringComparison.OrdinalIgnoreCase);
}
