using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class InstanceNameUniquenessRegression
{
    internal static async Task RunAsync()
    {
        await TestSequentialDuplicateAsync();
        await TestConcurrentDuplicateAsync();
    }

    private static async Task TestSequentialDuplicateAsync()
    {
        var root = NewRoot("sequential");
        try
        {
            var paths = new NexoPathService(root);
            var store = new InstanceStoreService(paths);

            var created = await store.CreateAsync(
                "  Test  ",
                "1.21.4");
            Assert(created.Name == "Test",
                "Instance name should be normalized before publication.");

            await ExpectDuplicateAsync(
                () => store.CreateAsync("test", "1.20.1"),
                "Sequential case-insensitive duplicate should be rejected.");

            var all = await store.GetAllAsync();
            Assert(all.Count == 1,
                "Rejected sequential duplicate must not publish another instance.");
            Assert(all[0].Id == created.Id,
                "Rejected sequential duplicate must preserve the original instance.");
            AssertPublishedDirectoryCount(paths, 1);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task TestConcurrentDuplicateAsync()
    {
        var root = NewRoot("concurrent");
        try
        {
            var paths = new NexoPathService(root);
            var storeA = new InstanceStoreService(paths);
            var storeB = new InstanceStoreService(paths);

            async Task<GameInstanceResult> AttemptAsync(
                InstanceStoreService store,
                string name,
                string version)
            {
                try
                {
                    var instance = await store.CreateAsync(name, version);
                    return new GameInstanceResult(instance.Id, null);
                }
                catch (Exception ex)
                {
                    return new GameInstanceResult(null, ex);
                }
            }

            var results = await Task.WhenAll(
                AttemptAsync(storeA, "RaceName", "1.21.4"),
                AttemptAsync(storeB, "racename", "1.20.1"));

            Assert(results.Count(result => result.Id is not null) == 1,
                "Exactly one concurrent case-insensitive create should succeed.");
            Assert(results.Count(result =>
                    result.Error is InvalidOperationException) == 1,
                "The losing concurrent create should fail with the duplicate-name invariant.");

            var all = await storeA.GetAllAsync();
            Assert(all.Count == 1,
                "Concurrent duplicate race must leave exactly one published instance.");
            Assert(
                all[0].Name.Equals(
                    "RaceName",
                    StringComparison.OrdinalIgnoreCase),
                "Published concurrent winner should retain the requested logical name.");
            AssertPublishedDirectoryCount(paths, 1);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task ExpectDuplicateAsync(
        Func<Task> action,
        string message)
    {
        try
        {
            await action();
        }
        catch (InvalidOperationException ex)
        {
            Assert(
                ex.Message.Contains(
                    "already exists",
                    StringComparison.OrdinalIgnoreCase),
                "Duplicate error should explain that the instance name already exists.");
            return;
        }

        throw new Exception(message);
    }

    private static void AssertPublishedDirectoryCount(
        NexoPathService paths,
        int expected)
    {
        var root = paths.GetInstancesRoot();
        var published = Directory.Exists(root)
            ? Directory.EnumerateDirectories(root)
                .Count(directory => File.Exists(
                    Path.Combine(directory, "instance.json")))
            : 0;
        Assert(published == expected,
            $"Expected {expected} published instance director{(expected == 1 ? "y" : "ies")}, got {published}.");
    }

    private static string NewRoot(string suffix)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-instance-name-tests",
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

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed record GameInstanceResult(
        string? Id,
        Exception? Error);
}
