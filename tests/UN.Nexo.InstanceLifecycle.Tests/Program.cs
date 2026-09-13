using System.IO.Compression;
using System.Text.Json;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.InstanceLifecycle.Tests;

internal static class Program
{
    private static async Task<int> Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "UN_Nexo-InstanceLifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new NexoPathService(root);
            var store = new InstanceStoreService(paths);
            var lifecycle = new InstanceLifecycleService(paths);

            var source = await store.CreateAsync("Source", "1.21.4");
            var sourceRoot = paths.GetInstanceDirectory(source.Id);
            var gameRoot = paths.GetInstanceGameDirectory(source.Id);
            Directory.CreateDirectory(gameRoot);
            await File.WriteAllTextAsync(Path.Combine(gameRoot, "options.txt"), "fov:90");
            await File.WriteAllTextAsync(
                Path.Combine(sourceRoot, "install-state.json"),
                JsonSerializer.Serialize(new { Id = source.Id, Name = source.Name, version = source.VersionId }, new JsonSerializerOptions { WriteIndented = true }));

            var worldA = Path.Combine(gameRoot, "saves", "World A");
            var worldB = Path.Combine(gameRoot, "saves", "World B", "data");
            Directory.CreateDirectory(worldA);
            Directory.CreateDirectory(worldB);
            await File.WriteAllTextAsync(Path.Combine(worldA, "level.dat"), "original-A");
            await File.WriteAllTextAsync(Path.Combine(worldB, "state.bin"), "original-B");

            await TestCloneWithoutWorldsAsync(lifecycle, paths, source);
            await TestCloneWithWorldsAsync(lifecycle, paths, source);
            var backup = await TestBackupAsync(lifecycle, source);
            await TestRestoreSafetyAsync(lifecycle, paths, source, backup);
            await TestCancelledCloneLeavesNoInstanceAsync(lifecycle, store, source);
            await TestMaliciousRestorePreservesCurrentWorldAsync(lifecycle, paths, source, backup);

            Console.WriteLine("PASS instance lifecycle regressions");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL instance lifecycle regressions");
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task TestCloneWithoutWorldsAsync(
        InstanceLifecycleService lifecycle,
        NexoPathService paths,
        GameInstance source)
    {
        var clone = await lifecycle.CloneAsync(source, "No worlds clone", includeWorlds: false);
        Require(clone.Id != source.Id, "Clone must receive a new id.");
        Require(clone.Name == "No worlds clone", "Clone name was not applied.");

        var cloneRoot = paths.GetInstanceDirectory(clone.Id);
        Require(File.Exists(Path.Combine(cloneRoot, "game", "options.txt")), "Clone should copy instance files.");
        Require(!Directory.Exists(Path.Combine(cloneRoot, "game", "saves")), "Worlds should be omitted when requested.");

        var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(cloneRoot, "instance.json")));
        Require(metadata.RootElement.GetProperty("id").GetString() == clone.Id, "instance.json should contain clone id.");

        var installStateText = await File.ReadAllTextAsync(Path.Combine(cloneRoot, "install-state.json"));
        Require(installStateText.Contains(clone.Id, StringComparison.Ordinal), "install-state should reference the clone id.");
        Require(!installStateText.Contains(source.Id, StringComparison.Ordinal), "install-state must not retain the source id.");
    }

    private static async Task TestCloneWithWorldsAsync(
        InstanceLifecycleService lifecycle,
        NexoPathService paths,
        GameInstance source)
    {
        var clone = await lifecycle.CloneAsync(source, "World clone", includeWorlds: true);
        var clonedLevel = Path.Combine(paths.GetInstanceGameDirectory(clone.Id), "saves", "World A", "level.dat");
        Require(File.Exists(clonedLevel), "World should be copied into full clone.");
        await File.WriteAllTextAsync(clonedLevel, "clone-only-change");
        var sourceLevel = Path.Combine(paths.GetInstanceGameDirectory(source.Id), "saves", "World A", "level.dat");
        Require(await File.ReadAllTextAsync(sourceLevel) == "original-A", "Clone must be independent from source files.");
    }

    private static async Task<WorldBackupInfo> TestBackupAsync(
        InstanceLifecycleService lifecycle,
        GameInstance source)
    {
        var backup = await lifecycle.CreateWorldBackupAsync(source, "pre-upgrade");
        Require(File.Exists(backup.FilePath), "Backup zip was not published.");
        Require(backup.Worlds.Count == 2, "Backup should contain both worlds.");
        Require(backup.Kind == "pre-upgrade", "Backup kind should be retained.");
        Require(!File.Exists(backup.FilePath + ".tmp"), "Temporary backup should not remain after success.");

        var listed = await lifecycle.GetBackupsAsync(source);
        Require(listed.Any(item => item.Id == backup.Id), "Backup list should include published backup.");
        return backup;
    }

    private static async Task TestRestoreSafetyAsync(
        InstanceLifecycleService lifecycle,
        NexoPathService paths,
        GameInstance source,
        WorldBackupInfo backup)
    {
        var levelPath = Path.Combine(paths.GetInstanceGameDirectory(source.Id), "saves", "World A", "level.dat");
        await File.WriteAllTextAsync(levelPath, "modified-after-backup");

        var result = await lifecycle.RestoreWorldAsync(source, backup, "World A");
        Require(await File.ReadAllTextAsync(levelPath) == "original-A", "Restore should recover backed-up world data.");
        Require(result.SafetyCopyPath is not null, "Existing world should be preserved as a safety copy.");
        Require(File.Exists(Path.Combine(result.SafetyCopyPath!, "level.dat")), "Safety copy should contain replaced world.");
        Require(await File.ReadAllTextAsync(Path.Combine(result.SafetyCopyPath!, "level.dat")) == "modified-after-backup",
            "Safety copy should preserve the pre-restore world.");
    }

    private static async Task TestCancelledCloneLeavesNoInstanceAsync(
        InstanceLifecycleService lifecycle,
        InstanceStoreService store,
        GameInstance source)
    {
        var before = await store.GetAllAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await lifecycle.CloneAsync(source, "Cancelled clone", includeWorlds: true, cancellation.Token);
            throw new InvalidOperationException("Cancelled clone unexpectedly succeeded.");
        }
        catch (OperationCanceledException)
        {
        }

        var after = await store.GetAllAsync();
        Require(after.Count == before.Count, "Cancelled clone must not publish a partial instance.");
        Require(after.All(item => item.Name != "Cancelled clone"), "Cancelled clone should not be visible.");
    }

    private static async Task TestMaliciousRestorePreservesCurrentWorldAsync(
        InstanceLifecycleService lifecycle,
        NexoPathService paths,
        GameInstance source,
        WorldBackupInfo goodBackup)
    {
        var levelPath = Path.Combine(paths.GetInstanceGameDirectory(source.Id), "saves", "World A", "level.dat");
        await File.WriteAllTextAsync(levelPath, "must-survive-malicious-restore");

        var maliciousPath = Path.Combine(Path.GetDirectoryName(goodBackup.FilePath)!, "malicious.zip");
        using (var archive = ZipFile.Open(maliciousPath, ZipArchiveMode.Create))
        {
            var manifest = archive.CreateEntry("manifest.json");
            await using (var stream = manifest.Open())
            {
                await JsonSerializer.SerializeAsync(stream, new
                {
                    schema = 1,
                    id = "malicious",
                    instanceId = source.Id,
                    instanceName = source.Name,
                    kind = "manual",
                    createdAt = DateTimeOffset.UtcNow,
                    worlds = new[]
                    {
                        new
                        {
                            name = "World A",
                            archivePrefix = "worlds/0000",
                            uncompressedBytes = 16,
                            fileCount = 1
                        }
                    }
                });
            }
            var evil = archive.CreateEntry("worlds/0000/../../outside.txt");
            await using var evilStream = new StreamWriter(evil.Open());
            await evilStream.WriteAsync("evil");
        }

        var malicious = new WorldBackupInfo(
            "malicious",
            source.Id,
            source.Name,
            "manual",
            DateTimeOffset.UtcNow,
            maliciousPath,
            new FileInfo(maliciousPath).Length,
            [new WorldBackupWorld("World A", "worlds/0000", 16, 1)]);

        try
        {
            await lifecycle.RestoreWorldAsync(source, malicious, "World A");
            throw new InvalidOperationException("Malicious archive unexpectedly restored.");
        }
        catch (InvalidDataException)
        {
        }

        Require(await File.ReadAllTextAsync(levelPath) == "must-survive-malicious-restore",
            "Failed restore must leave the active world untouched.");
        Require(!File.Exists(Path.Combine(paths.GetInstanceDirectory(source.Id), "outside.txt")),
            "Zip traversal must not write outside staging.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
