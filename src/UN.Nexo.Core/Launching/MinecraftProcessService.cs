using System.Diagnostics;
using System.Runtime.InteropServices;
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
    private readonly LauncherRuntimeSettingsService _runtimeSettings;
    private int _running;

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
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            throw new InvalidOperationException("A game is already running.");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var runtime = await _runtimeSettings.LoadAsync(cancellationToken);
            var effectivePlan = plan with
            {
                Arguments = RuntimeLaunchOptions.Apply(plan.Arguments, runtime)
            };

            Directory.CreateDirectory(effectivePlan.LogDirectory);
            var logPath = Path.Combine(
                effectivePlan.LogDirectory,
                $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");

            await using var log = new StreamWriter(
                new FileStream(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true
            };

            using var logLock = new SemaphoreSlim(1, 1);

            async Task WriteLogAsync(string message)
            {
                await logLock.WaitAsync(CancellationToken.None);
                try
                {
                    await log.WriteLineAsync(message);
                }
                finally
                {
                    logLock.Release();
                }
            }

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
            Interlocked.Exchange(ref _running, 0);
        }
    }
}
