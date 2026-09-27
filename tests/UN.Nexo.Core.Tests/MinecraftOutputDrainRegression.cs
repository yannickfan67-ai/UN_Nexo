using System.Diagnostics;
using System.Globalization;
using UN.Nexo.Core.Launching;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class MinecraftOutputDrainRegression
{
    private const int ParentExitCode = 23;

    internal static async Task RunAsync()
    {
        await TestRetainedPipeDoesNotHangAsync();
        await TestCancellationAfterParentExitAsync();
    }

    internal static async Task<int> RunParentHelperAsync(string[] args)
    {
        if (args.Length < 2)
            return 2;

        var childPidPath = args[1];
        using var child = Process.Start(
            BuildSelfStartInfo("--minecraft-output-holder-child"))
            ?? throw new InvalidOperationException(
                "Could not start inherited-output child helper.");

        await File.WriteAllTextAsync(
            childPidPath,
            child.Id.ToString(CultureInfo.InvariantCulture));
        await Console.Out.WriteLineAsync("PARENT-EXIT");
        await Console.Error.WriteLineAsync("PARENT-ERR");
        await Console.Out.FlushAsync();
        await Console.Error.FlushAsync();
        return ParentExitCode;
    }

    internal static async Task<int> RunChildHelperAsync()
    {
        await Console.Out.WriteLineAsync("CHILD-HOLD");
        await Console.Out.FlushAsync();
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }

    internal static Task<int> RunQuickExitHelperAsync()
        => Task.FromResult(0);

    private static async Task TestRetainedPipeDoesNotHangAsync()
    {
        var root = NewRoot("bounded");
        var childPidPath = Path.Combine(root, "child.pid");
        var childPid = 0;
        var oldGrace = LaunchDiagnostics.OutputDrainGracePeriod;

        try
        {
            LaunchDiagnostics.OutputDrainGracePeriod =
                TimeSpan.FromMilliseconds(250);

            var service = new MinecraftProcessService(
                new LauncherRuntimeSettingsService(
                    new NexoPathService(Path.Combine(root, "data"))));
            var instanceId = Guid.NewGuid().ToString("N");
            var plan = BuildPlan(
                root,
                instanceId,
                "--minecraft-output-holder-parent",
                childPidPath);

            using var timeout =
                new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var stopwatch = Stopwatch.StartNew();
            var result = await service.RunAsync(
                plan,
                cancellationToken: timeout.Token);
            stopwatch.Stop();

            Assert(
                result.ExitCode == ParentExitCode,
                "The direct helper exit code must be preserved.");
            Assert(
                !timeout.IsCancellationRequested,
                "Retained descendant output handles must not force the fixture timeout.");
            Assert(
                stopwatch.Elapsed < TimeSpan.FromSeconds(4),
                "Post-exit output draining exceeded its bounded grace period.");

            childPid = await ReadPidWithRetryAsync(
                childPidPath,
                TimeSpan.FromSeconds(1));

            using var restartTimeout =
                new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var restart = await service.RunAsync(
                BuildPlan(
                    root,
                    instanceId,
                    "--minecraft-output-quick-exit"),
                cancellationToken: restartTimeout.Token);
            Assert(
                restart.ExitCode == 0,
                "The instance running marker must be released after bounded drain shutdown.");
        }
        finally
        {
            LaunchDiagnostics.OutputDrainGracePeriod = oldGrace;
            KillProcess(childPid);
            TryDelete(root);
        }
    }

    private static async Task TestCancellationAfterParentExitAsync()
    {
        var root = NewRoot("cancel-after-exit");
        var childPidPath = Path.Combine(root, "child.pid");
        var childPid = 0;
        var oldGrace = LaunchDiagnostics.OutputDrainGracePeriod;

        try
        {
            LaunchDiagnostics.OutputDrainGracePeriod =
                TimeSpan.FromSeconds(30);

            var service = new MinecraftProcessService(
                new LauncherRuntimeSettingsService(
                    new NexoPathService(Path.Combine(root, "data"))));
            var parentPid = new TaskCompletionSource<int>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource();
            var runTask = service.RunAsync(
                BuildPlan(
                    root,
                    Guid.NewGuid().ToString("N"),
                    "--minecraft-output-holder-parent",
                    childPidPath),
                new InlineProgress(message =>
                {
                    const string prefix = "Started game process ";
                    if (message.StartsWith(prefix, StringComparison.Ordinal)
                        && int.TryParse(
                            message[prefix.Length..],
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out var pid))
                    {
                        parentPid.TrySetResult(pid);
                    }
                }),
                cancellation.Token);

            var directPid = await parentPid.Task.WaitAsync(
                TimeSpan.FromSeconds(3));
            childPid = await ReadPidWithRetryAsync(
                childPidPath,
                TimeSpan.FromSeconds(1));
            await WaitForProcessExitAsync(
                directPid,
                TimeSpan.FromSeconds(3));

            var stopwatch = Stopwatch.StartNew();
            cancellation.Cancel();
            try
            {
                await runTask.WaitAsync(TimeSpan.FromSeconds(3));
                throw new Exception(
                    "Cancellation after the direct process exited unexpectedly completed normally.");
            }
            catch (OperationCanceledException)
            {
            }
            stopwatch.Stop();

            Assert(
                stopwatch.Elapsed < TimeSpan.FromSeconds(2),
                "Caller cancellation must interrupt post-exit output draining promptly.");
        }
        finally
        {
            LaunchDiagnostics.OutputDrainGracePeriod = oldGrace;
            KillProcess(childPid);
            TryDelete(root);
        }
    }

    private static MinecraftLaunchPlan BuildPlan(
        string root,
        string instanceId,
        string helperMode,
        params string[] helperArguments)
    {
        var executable =
            Environment.ProcessPath
            ?? throw new InvalidOperationException(
                "Current test executable path is unavailable.");
        var arguments = new List<string>();

        if (Path.GetFileNameWithoutExtension(executable)
            .Equals(
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add(
                typeof(MinecraftOutputDrainRegression)
                    .Assembly
                    .Location);
        }

        arguments.Add(helperMode);
        arguments.AddRange(helperArguments);

        return new MinecraftLaunchPlan(
            executable,
            root,
            arguments,
            Path.Combine(root, "logs"))
        {
            InstanceId = instanceId
        };
    }

    private static ProcessStartInfo BuildSelfStartInfo(
        string helperMode)
    {
        var executable =
            Environment.ProcessPath
            ?? throw new InvalidOperationException(
                "Current test executable path is unavailable.");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false
        };

        if (Path.GetFileNameWithoutExtension(executable)
            .Equals(
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(
                typeof(MinecraftOutputDrainRegression)
                    .Assembly
                    .Location);
        }

        startInfo.ArgumentList.Add(helperMode);
        return startInfo;
    }

    private static async Task<int> ReadPidWithRetryAsync(
        string path,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(path))
                {
                    var text = await File.ReadAllTextAsync(path);
                    if (int.TryParse(
                            text.Trim(),
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out var pid)
                        && pid > 0)
                    {
                        return pid;
                    }
                }
            }
            catch (Exception ex) when (
                ex is IOException
                or UnauthorizedAccessException)
            {
            }

            await Task.Delay(20);
        }

        throw new InvalidOperationException(
            "Inherited-output child helper did not publish its PID.");
    }

    private static async Task WaitForProcessExitAsync(
        int pid,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited)
                    return;
            }
            catch (ArgumentException)
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new InvalidOperationException(
            "Direct helper process did not exit within the fixture deadline.");
    }

    private static void KillProcess(int pid)
    {
        if (pid <= 0)
            return;

        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static string NewRoot(string suffix)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-minecraft-output-drain",
            suffix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private static void Assert(
        bool condition,
        string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class InlineProgress(Action<string> report)
        : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
