using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UN.Nexo.Core.Launching;

namespace UN.Nexo.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    [ObservableProperty] private bool isGameStarting;
    [ObservableProperty] private bool launchDebugEnabled = true;
    [ObservableProperty] private string launchDebugSummary = "Debug trace enabled · launch phases and Java process health will be recorded.";
    [ObservableProperty] private string lastLaunchTracePath = string.Empty;

    private StreamWriter? _launchTraceWriter;
    private string? _lastTracedGameLine;
    private bool _debugProcessStarted;

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
        if (IsGameRunning && _launchTraceWriter is null && IsLaunchBeginning(value))
            StartDebugTrace();

        if (_launchTraceWriter is not null)
            WriteDebugTrace($"[status] {value}");

        if (IsTerminalStatus(value))
        {
            IsGameStarting = false;
            LaunchDebugSummary = value;
            CloseDebugTrace();
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
            LaunchDebugSummary = $"Minecraft process log · {line[5..]}";
        }
    }

    [RelayCommand]
    private void OpenDebugFolder()
    {
        try
        {
            var directory = Path.Combine(_paths.GetDataRoot(), "logs");
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            LaunchDebugSummary = $"Could not open debug folder: {ex.Message}";
        }
    }

    private void StartDebugTrace()
    {
        CloseDebugTrace();
        var directory = Path.Combine(_paths.GetDataRoot(), "logs");
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

        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
        WriteDebugTrace($"[trace] UN_Nexo {version}");
        WriteDebugTrace($"[trace] Started {DateTimeOffset.Now:O}");
        WriteDebugTrace($"[trace] OS {RuntimeInformation.OSDescription.Trim()} · {RuntimeInformation.OSArchitecture} · process {RuntimeInformation.ProcessArchitecture}");
        WriteDebugTrace($"[trace] Instance {SelectedInstance?.Name ?? "none"} · Minecraft {SelectedInstance?.VersionId ?? "unknown"}");
        WriteDebugTrace($"[trace] Account {SelectedAccount?.DisplayName ?? "none"} · source {_downloadSources.DisplayName}");
        WriteDebugTrace($"[trace] Data root {_paths.GetDataRoot()}");
        LaunchDebugSummary = $"Tracing launch · {Path.GetFileName(LastLaunchTracePath)}";
    }

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
}
