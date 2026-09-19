using UN.Nexo.Core.Launching;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class ProcessConcurrencyRegression
{
    internal static async Task RunAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP process concurrency fixture uses a Unix helper script.");
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-process-concurrency-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var helper = Path.Combine(root, "fake-java");
        await File.WriteAllTextAsync(
            helper,
            "#!/bin/sh\nwhile :; do sleep 1; done\n");
        File.SetUnixFileMode(
            helper,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        try
        {
            var runtimeSettings = new LauncherRuntimeSettingsService(
                new NexoPathService(root));
            var serviceA = new MinecraftProcessService(runtimeSettings);
            var serviceB = new MinecraftProcessService(runtimeSettings);

            var idA = Guid.NewGuid().ToString("N");
            var idB = Guid.NewGuid().ToString("N");
            var planA = Plan(root, helper, idA, "a");
            var planB = Plan(root, helper, idB, "b");

            using var cancelA = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var startedA = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var taskA = serviceA.RunAsync(
                planA,
                new InlineProgress(message =>
                {
                    if (message.StartsWith("Started game process ", StringComparison.Ordinal))
                        startedA.TrySetResult(true);
                }),
                cancelA.Token);
            await startedA.Task.WaitAsync(TimeSpan.FromSeconds(3));

            try
            {
                await serviceB.RunAsync(planA);
                throw new Exception(
                    "A duplicate launch for the same instance should be rejected across service objects.");
            }
            catch (InvalidOperationException ex)
            {
                Assert(
                    ex.Message.Contains("already running", StringComparison.OrdinalIgnoreCase),
                    "Same-instance duplicate rejection should explain that the instance is running.");
            }

            using var cancelB = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var startedB = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var taskB = serviceA.RunAsync(
                planB,
                new InlineProgress(message =>
                {
                    if (message.StartsWith("Started game process ", StringComparison.Ordinal))
                        startedB.TrySetResult(true);
                }),
                cancelB.Token);
            await startedB.Task.WaitAsync(TimeSpan.FromSeconds(3));

            Assert(!taskA.IsCompleted && !taskB.IsCompleted,
                "Different instance IDs should remain alive concurrently.");

            cancelA.Cancel();
            await ExpectCanceledAsync(taskA, "instance A cancellation");
            await Task.Delay(100);
            Assert(!taskB.IsCompleted,
                "Cancelling instance A must not terminate or clear instance B.");

            cancelB.Cancel();
            await ExpectCanceledAsync(taskB, "instance B cancellation");

            using var cancelRestart = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var restarted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var restartTask = serviceB.RunAsync(
                planA,
                new InlineProgress(message =>
                {
                    if (message.StartsWith("Started game process ", StringComparison.Ordinal))
                        restarted.TrySetResult(true);
                }),
                cancelRestart.Token);
            await restarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancelRestart.Cancel();
            await ExpectCanceledAsync(
                restartTask,
                "same instance should start again after prior process exits");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static MinecraftLaunchPlan Plan(
        string root,
        string helper,
        string instanceId,
        string suffix)
    {
        var working = Path.Combine(root, "work-" + suffix);
        var logs = Path.Combine(root, "logs-" + suffix);
        Directory.CreateDirectory(working);
        Directory.CreateDirectory(logs);
        return new MinecraftLaunchPlan(
            helper,
            working,
            [],
            logs)
        {
            InstanceId = instanceId
        };
    }

    private static async Task ExpectCanceledAsync(
        Task<MinecraftExitResult> task,
        string label)
    {
        try
        {
            _ = await task;
            throw new Exception(label + " unexpectedly completed normally.");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
