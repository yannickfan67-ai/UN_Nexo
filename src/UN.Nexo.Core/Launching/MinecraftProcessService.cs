using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Launching;

public sealed record MinecraftExitResult(int ExitCode, string LogPath);

public static class LaunchDiagnostics
{
    public static bool Enabled { get; set; } = true;
    public static TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(10);
}

public sealed class MinecraftProcessService
{
    internal const long MaxOwnedLogBytes = 8L * 1024 * 1024;
    internal const int MaxOwnedLogCount = 20;
    internal const long MaxOwnedLogDirectoryBytes = 64L * 1024 * 1024;

    private static readonly Regex OwnedLogNameRegex = new(
        @"^\d{8}-\d{6}-[0-9a-f]{32}\.log$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly ConcurrentDictionary<string, byte> RunningInstances =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly LauncherRuntimeSettingsService _runtimeSettings;

    public MinecraftProcessService()
        : this(new LauncherRuntimeSettingsService(new NexoPathService()))
    {
    }

    public MinecraftProcessService(LauncherRuntimeSettingsService runtimeSettings)
    {
        _runtimeSettings = runtimeSettings;
    }

    public async Task<MinecraftExitResult> RunAsync(
        MinecraftLaunchPlan plan,
        IProgress<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        var instanceId = string.IsNullOrWhiteSpace(plan.InstanceId)
            ? null
            : plan.InstanceId.Trim();
        var registered = false;
        if (instanceId is not null)
        {
            registered = RunningInstances.TryAdd(instanceId, 0);
            if (!registered)
                throw new InvalidOperationException(
                    $"Minecraft instance '{instanceId}' is already running.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var runtime = await _runtimeSettings.LoadAsync(cancellationToken);
            var effectivePlan = plan with
            {
                Arguments = RuntimeLaunchOptions.Apply(plan.Arguments, runtime)
            };

            Directory.CreateDirectory(effectivePlan.LogDirectory);
            PruneOwnedLogs(
                effectivePlan.LogDirectory,
                MaxOwnedLogCount - 1,
                MaxOwnedLogDirectoryBytes - MaxOwnedLogBytes);

            var logPath = Path.Combine(
                effectivePlan.LogDirectory,
                $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");

            await using var log = new BoundedLaunchLogWriter(logPath, MaxOwnedLogBytes);

            Task WriteLogAsync(string message)
                => log.WriteLineAsync(message);

            void Report(string message) => output?.Report(message);

            var startInfo = effectivePlan.CreateStartInfo();
            using var process = new Process { StartInfo = startInfo };
            var heap = effectivePlan.Arguments.FirstOrDefault(argument =>
                argument.StartsWith("-Xmx", StringComparison.OrdinalIgnoreCase)) ?? "default memory";
            var nativePath = effectivePlan.Arguments.FirstOrDefault(argument =>
                argument.StartsWith("-Djava.library.path=", StringComparison.OrdinalIgnoreCase));
            var mainClass = effectivePlan.Arguments.FirstOrDefault(argument =>
                argument.StartsWith("net.minecraft.", StringComparison.Ordinal)
                || argument.StartsWith("com.mojang.", StringComparison.Ordinal));
            string? classPath = null;
            for (var index = 0; index < effectivePlan.Arguments.Count - 1; index++)
            {
                if (effectivePlan.Arguments[index] is "-cp" or "-classpath")
                {
                    classPath = effectivePlan.Arguments[index + 1];
                    break;
                }
            }

            Report($"Log: {logPath}");
            Report($"Runtime: {heap}");
            await WriteLogAsync($"[launcher] Session started: {DateTimeOffset.Now:O}");
            await WriteLogAsync($"[launcher] Java: {startInfo.FileName}");
            await WriteLogAsync($"[launcher] Working directory: {startInfo.WorkingDirectory}");
            await WriteLogAsync($"[launcher] Runtime: {heap}");

            if (LaunchDiagnostics.Enabled)
            {
                var classPathEntries = classPath?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Length ?? 0;
                var osDescription = RuntimeInformation.OSDescription.Trim();
                var debugLines = new[]
                {
                    $"[debug] OS: {osDescription} · {RuntimeInformation.OSArchitecture} · process {RuntimeInformation.ProcessArchitecture}",
                    $"[debug] Java executable: {startInfo.FileName}",
                    $"[debug] Working directory: {startInfo.WorkingDirectory}",
                    $"[debug] Arguments: {effectivePlan.Arguments.Count} · classpath entries: {classPathEntries}",
                    $"[debug] Main class: {mainClass ?? "not detected"}",
                    $"[debug] Native path: {nativePath ?? "not supplied"}",
                    $"[debug] Heap: {heap}"
                };
                foreach (var line in debugLines)
                {
                    await WriteLogAsync(line);
                    Report(line);
                }
            }

            var startTimer = Stopwatch.StartNew();
            try
            {
                if (!process.Start())
                    throw new InvalidOperationException("Java could not be started.");
            }
            catch (Exception ex)
            {
                await WriteLogAsync($"[launcher] Process start failed after {startTimer.ElapsedMilliseconds} ms: {ex}");
                Report($"[debug] Process start failed: {ex.GetType().Name}: {ex.Message}");
                throw;
            }

            using var stopRegistration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
            });

            var lastOutputTimestamp = Stopwatch.GetTimestamp();
            var firstOutputSeen = 0;
            using var heartbeatCancellation = new CancellationTokenSource();

            async Task HeartbeatAsync()
            {
                try
                {
                    while (!heartbeatCancellation.IsCancellationRequested)
                    {
                        await Task.Delay(LaunchDiagnostics.HeartbeatInterval, heartbeatCancellation.Token);
                        if (heartbeatCancellation.IsCancellationRequested || process.HasExited)
                            break;
                        if (!LaunchDiagnostics.Enabled)
                            continue;

                        var silentFor = Stopwatch.GetElapsedTime(Volatile.Read(ref lastOutputTimestamp));
                        long workingSet = 0;
                        try { workingSet = process.WorkingSet64; } catch { }
                        var memoryText = workingSet > 0
                            ? $" · working set {workingSet / 1024d / 1024d:0.0} MiB"
                            : string.Empty;
                        var heartbeat = $"[debug] PID {process.Id} alive {startTimer.Elapsed.TotalSeconds:0}s · output quiet {silentFor.TotalSeconds:0}s{memoryText}";
                        await WriteLogAsync(heartbeat);
                        Report(heartbeat);
                    }
                }
                catch (OperationCanceledException) when (heartbeatCancellation.IsCancellationRequested)
                {
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                }
            }

            async Task DrainAsync(StreamReader reader, string channel)
            {
                while (await reader.ReadLineAsync() is { } line)
                {
                    Volatile.Write(ref lastOutputTimestamp, Stopwatch.GetTimestamp());
                    if (LaunchDiagnostics.Enabled && Interlocked.CompareExchange(ref firstOutputSeen, 1, 0) == 0)
                    {
                        var firstOutput = $"[debug] First Minecraft output after {startTimer.ElapsedMilliseconds} ms";
                        await WriteLogAsync(firstOutput);
                        Report(firstOutput);
                    }

                    var message = $"[{channel}] {line}";
                    await WriteLogAsync(message);
                    Report(message.Length > 4096 ? message[..4096] + "…" : message);
                }
            }

            // A failed output consumer must stop Java immediately: otherwise its redirected
            // pipe can fill while WaitForExitAsync waits forever for Java to finish.
            void StopProcess()
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
            }

            async Task SuperviseAsync(Task task)
            {
                try { await task; }
                catch
                {
                    StopProcess();
                    throw;
                }
            }

            Task stdout = Task.CompletedTask;
            Task stderr = Task.CompletedTask;
            Task heartbeatTask = Task.CompletedTask;
            try
            {
                var startedMessage = $"Started game process {process.Id}";
                Report(startedMessage);
                await WriteLogAsync($"[launcher] {startedMessage} after {startTimer.ElapsedMilliseconds} ms");

                stdout = SuperviseAsync(DrainAsync(process.StandardOutput, "stdout"));
                stderr = SuperviseAsync(DrainAsync(process.StandardError, "stderr"));
                heartbeatTask = SuperviseAsync(HeartbeatAsync());
                await process.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(stdout, stderr);
                heartbeatCancellation.Cancel();
                await heartbeatTask;
                await WriteLogAsync($"[launcher] Exit code: {process.ExitCode} · lifetime {startTimer.Elapsed.TotalSeconds:0.000}s");

                cancellationToken.ThrowIfCancellationRequested();
                return new MinecraftExitResult(process.ExitCode, logPath);
            }
            finally
            {
                heartbeatCancellation.Cancel();
                StopProcess();
                await process.WaitForExitAsync(CancellationToken.None);
                // Observe every worker before disposing the log, lock and process. Preserve
                // the original failure already propagated by the try block.
                try { await Task.WhenAll(stdout, stderr, heartbeatTask); }
                catch { }
            }
        }
        finally
        {
            if (registered && instanceId is not null)
                RunningInstances.TryRemove(instanceId, out _);
        }
    }

    internal static void PruneOwnedLogs(
        string logDirectory,
        int maxCount = MaxOwnedLogCount,
        long maxBytes = MaxOwnedLogDirectoryBytes)
    {
        if (maxCount < 0)
            throw new ArgumentOutOfRangeException(nameof(maxCount));
        if (maxBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (!Directory.Exists(logDirectory))
            return;

        FileInfo[] owned;
        try
        {
            owned = new DirectoryInfo(logDirectory)
                .EnumerateFiles("*.log", SearchOption.TopDirectoryOnly)
                .Where(file => OwnedLogNameRegex.IsMatch(file.Name))
                .OrderBy(file => file.LastWriteTimeUtc)
                .ThenBy(file => file.Name, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex) when (
            ex is IOException
            or UnauthorizedAccessException
            or DirectoryNotFoundException)
        {
            return;
        }

        long total = 0;
        foreach (var file in owned)
        {
            try
            {
                total = checked(total + Math.Max(0, file.Length));
            }
            catch (IOException)
            {
            }
            catch (OverflowException)
            {
                total = long.MaxValue;
            }
        }

        var remaining = owned.Length;
        foreach (var file in owned)
        {
            if (remaining <= maxCount && total <= maxBytes)
                break;

            long length;
            try
            {
                length = Math.Max(0, file.Length);
                file.Delete();
            }
            catch (Exception ex) when (
                ex is IOException
                or UnauthorizedAccessException)
            {
                continue;
            }

            remaining--;
            total = Math.Max(0, total - length);
        }
    }

    private sealed class BoundedLaunchLogWriter : IAsyncDisposable
    {
        private const string TruncationMarker =
            "[launcher] Log persistence limit reached; further process output is still being drained but is not written to this file.";

        private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
        private static readonly byte[] NewLineBytes = Utf8.GetBytes(Environment.NewLine);
        private static readonly byte[] MarkerBytes =
            Utf8.GetBytes(TruncationMarker + Environment.NewLine);

        private readonly FileStream _stream;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly long _maxBytes;
        private long _written;
        private bool _truncated;

        public BoundedLaunchLogWriter(string path, long maxBytes)
        {
            if (maxBytes < MarkerBytes.Length)
                throw new ArgumentOutOfRangeException(nameof(maxBytes));

            _maxBytes = maxBytes;
            _stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }

        public async Task WriteLineAsync(string message)
        {
            await _gate.WaitAsync(CancellationToken.None);
            try
            {
                if (_truncated)
                    return;

                var payloadBytes = Utf8.GetByteCount(message);
                var required = checked((long)payloadBytes + NewLineBytes.Length);
                if (_written + required + MarkerBytes.Length <= _maxBytes)
                {
                    var payload = Utf8.GetBytes(message);
                    await _stream.WriteAsync(payload);
                    await _stream.WriteAsync(NewLineBytes);
                    _written += required;
                    await _stream.FlushAsync();
                    return;
                }

                if (_written + MarkerBytes.Length <= _maxBytes)
                {
                    await _stream.WriteAsync(MarkerBytes);
                    _written += MarkerBytes.Length;
                    await _stream.FlushAsync();
                }

                _truncated = true;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync(CancellationToken.None);
            try
            {
                await _stream.FlushAsync();
                await _stream.DisposeAsync();
            }
            finally
            {
                _gate.Release();
                _gate.Dispose();
            }
        }
    }
}
