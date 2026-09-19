using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class InstanceOperationCoordinatorRegression
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-instance-operation-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var paths = new NexoPathService(root);
            paths.EnsureDirectories();

            await TestReentrantAsync(paths);
            await TestSameInstanceSerializesAsync(paths);
            await TestDifferentInstancesIndependentAsync(paths);
            await TestCrossProcessBusyAsync(paths);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static async Task TestReentrantAsync(
        NexoPathService paths)
    {
        var id = Guid.NewGuid().ToString("N");
        var first =
            new InstanceOperationCoordinator(paths);
        var second =
            new InstanceOperationCoordinator(paths);
        using var guard =
            new CancellationTokenSource(
                TimeSpan.FromSeconds(2));

        using var outer = await first.AcquireAsync(
            id,
            "outer",
            guard.Token);
        using var inner = await second.AcquireAsync(
            id,
            "nested",
            guard.Token);

        Assert(
            !guard.IsCancellationRequested,
            "Nested same-flow instance operation should re-enter without waiting on itself.");
    }

    private static async Task TestSameInstanceSerializesAsync(
        NexoPathService paths)
    {
        var id = Guid.NewGuid().ToString("N");
        var first =
            new InstanceOperationCoordinator(paths);
        var second =
            new InstanceOperationCoordinator(paths);
        var outer = await first.AcquireAsync(
            id,
            "first");

        var entered =
            new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        Task secondOperation;
        using (ExecutionContext.SuppressFlow())
        {
            secondOperation = Task.Run(async () =>
            {
                using var lease =
                    await second.AcquireAsync(
                        id,
                        "second");
                entered.TrySetResult();
            });
        }

        await Task.Delay(100);
        Assert(
            !entered.Task.IsCompleted,
            "An independent operation on the same instance must wait for the active lease.");

        outer.Dispose();
        await entered.Task.WaitAsync(
            TimeSpan.FromSeconds(2));
        await secondOperation;
    }

    private static async Task TestDifferentInstancesIndependentAsync(
        NexoPathService paths)
    {
        var firstId =
            Guid.NewGuid().ToString("N");
        var secondId =
            Guid.NewGuid().ToString("N");
        var first =
            new InstanceOperationCoordinator(paths);
        var second =
            new InstanceOperationCoordinator(paths);
        using var guard =
            new CancellationTokenSource(
                TimeSpan.FromSeconds(2));

        using var firstLease =
            await first.AcquireAsync(
                firstId,
                "first instance",
                guard.Token);
        using var secondLease =
            await second.AcquireAsync(
                secondId,
                "second instance",
                guard.Token);

        Assert(
            !guard.IsCancellationRequested,
            "Different instances should not block one another.");
    }

    private static async Task TestCrossProcessBusyAsync(
        NexoPathService paths)
    {
        var id = Guid.NewGuid().ToString("N");
        var instanceRoot =
            paths.GetInstanceDirectory(id);
        Directory.CreateDirectory(instanceRoot);
        var lockPath = Path.Combine(
            instanceRoot,
            InstanceOperationCoordinator.LockFileName);

        using var external = new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        var coordinator =
            new InstanceOperationCoordinator(paths);
        try
        {
            using var _ = await coordinator.AcquireAsync(
                id,
                "blocked operation");
            throw new Exception(
                "Cross-process instance lock should have rejected the operation.");
        }
        catch (InvalidOperationException ex)
        {
            Assert(
                ex.Message.Contains(
                    "another UN_Nexo process",
                    StringComparison.OrdinalIgnoreCase),
                "Cross-process collision should produce an actionable busy message.");
        }
    }

    private static void Assert(
        bool condition,
        string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
