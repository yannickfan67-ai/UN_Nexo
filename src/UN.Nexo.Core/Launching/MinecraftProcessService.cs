using System.Diagnostics;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Launching;

public sealed record MinecraftExitResult(int ExitCode, string LogPath);

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
            using var process = new Process { StartInfo = effectivePlan.CreateStartInfo() };
            output?.Report($"Log: {logPath}");
            output?.Report($"Runtime: {effectivePlan.Arguments.FirstOrDefault(argument => argument.StartsWith("-Xmx", StringComparison.OrdinalIgnoreCase)) ?? "default memory"}");

            if (!process.Start())
                throw new InvalidOperationException("Java could not be started.");

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

            output?.Report($"Started game process {process.Id}");

            async Task DrainAsync(StreamReader reader, string channel)
            {
                while (await reader.ReadLineAsync() is { } line)
                {
                    var message = $"[{channel}] {line}";
                    await logLock.WaitAsync();
                    try
                    {
                        await log.WriteLineAsync(message);
                    }
                    finally
                    {
                        logLock.Release();
                    }

                    output?.Report(message.Length > 4096 ? message[..4096] + "…" : message);
                }
            }

            var stdout = DrainAsync(process.StandardOutput, "stdout");
            var stderr = DrainAsync(process.StandardError, "stderr");
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            await log.WriteLineAsync($"[launcher] Exit code: {process.ExitCode}");

            cancellationToken.ThrowIfCancellationRequested();
            return new MinecraftExitResult(process.ExitCode, logPath);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }
}
