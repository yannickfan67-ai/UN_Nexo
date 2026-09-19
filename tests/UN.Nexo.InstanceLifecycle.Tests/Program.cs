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
            await File.WriteAllTextAsync(Path.Combine(worldA, "region.mca"), "region-data");
            await File.WriteAllTextAsync(Path.Combine(worldB, "state.bin"), "original-B");

            await TestCloneWithoutWorldsAsync(lifecycle, paths, source);
            await TestCloneWithWorldsAsync(lifecycle, paths, source);
            await TestInvalidInstallStateCloneAsync(lifecycle, paths);
            await TestFabricCloneMetadataAsync(lifecycle, paths);
            source = await TestRenameAsync(lifecycle, store, paths, source);
            await TestDuplicateRenameRejectedAsync(lifecycle, store, source);
            await TestDeletePreservesBackupsAsync(lifecycle, store, paths);
            await TestDeleteRemovesBackupsAsync(lifecycle, store, paths);
            await TestDeleteDoesNotFollowNestedLinkAsync(lifecycle, store, paths);
            await TestDeleteRejectsLinkedBackupRootAsync(lifecycle, store, paths);
            await TestCancelledDeletePreservesInstanceAsync(lifecycle, store, paths);
            var backup = await TestBackupAsync(lifecycle, source);
            await BackupIntegrityRegression.RunAsync(lifecycle, paths, source, backup);
            await TestRestoreSafetyAsync(lifecycle, paths, source, backup);
            await TestIncompleteRestorePreservesCurrentWorldAsync(lifecycle, paths, source, backup);
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

        var installStatePath = Path.Combine(cloneRoot, "install-state.json");
        var installStateText = await File.ReadAllTextAsync(installStatePath);
        Require(installStateText.Contains(clone.Id, StringComparison.Ordinal), "install-state should reference the clone id.");
        Require(installStateText.Contains(clone.Name, StringComparison.Ordinal), "install-state should reference the clone name.");
        Require(!installStateText.Contains(source.Id, StringComparison.Ordinal), "install-state must not retain the source id.");
        Require(!installStateText.Contains(source.Name, StringComparison.Ordinal), "install-state must not retain the source name.");
        Require(
            !Directory.EnumerateFiles(cloneRoot, "install-state.json.*.tmp", SearchOption.TopDirectoryOnly).Any(),
            "Clone install-state atomic rewrite should not leave temp files.");
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

    private static async Task TestInvalidInstallStateCloneAsync(
        InstanceLifecycleService lifecycle,
        NexoPathService paths)
    {
        var fixtures = new[]
        {
            ("malformed", "{not-json"),
            ("scalar", "\"source-bound-state\"")
        };

        foreach (var fixture in fixtures)
        {
            var source = new GameInstance(
                Guid.NewGuid().ToString("N"),
                "Invalid state source " + fixture.Item1,
                "1.21.4",
                "vanilla",
                DateTimeOffset.UtcNow.AddMinutes(-1));
            var sourceRoot = paths.GetInstanceDirectory(source.Id);
            var gameRoot = paths.GetInstanceGameDirectory(source.Id);
            Directory.CreateDirectory(gameRoot);
            await File.WriteAllTextAsync(
                Path.Combine(sourceRoot, "instance.json"),
                JsonSerializer.Serialize(
                    source,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)
                    {
                        WriteIndented = true
                    }));
            await File.WriteAllTextAsync(
                Path.Combine(sourceRoot, "install-state.json"),
                fixture.Item2);
            await File.WriteAllTextAsync(
                Path.Combine(gameRoot, "marker.txt"),
                fixture.Item1);

            var clone = await lifecycle.CloneAsync(
                source,
                "Invalid state clone " + fixture.Item1,
                includeWorlds: false);
            var cloneRoot = paths.GetInstanceDirectory(clone.Id);

            Require(
                !File.Exists(Path.Combine(cloneRoot, "install-state.json")),
                $"Clone with {fixture.Item1} install state must publish as unprepared.");
            Require(
                await File.ReadAllTextAsync(Path.Combine(cloneRoot, "game", "marker.txt"))
                    == fixture.Item1,
                $"Clone with {fixture.Item1} state should still copy ordinary instance files.");
            Require(
                await File.ReadAllTextAsync(Path.Combine(sourceRoot, "install-state.json"))
                    == fixture.Item2,
                $"Clone must not alter the source {fixture.Item1} install state.");
        }
    }

    private static async Task TestFabricCloneMetadataAsync(
        InstanceLifecycleService lifecycle,
        NexoPathService paths)
    {
        var source = new GameInstance(
            Guid.NewGuid().ToString("N"),
            "Fabric clone source",
            "fabric-loader-0.16.9-1.21.4",
            "fabric",
            DateTimeOffset.UtcNow.AddMinutes(-5),
            "1.21.4",
            "0.16.9");
        var sourceRoot = paths.GetInstanceDirectory(source.Id);
        Directory.CreateDirectory(paths.GetInstanceGameDirectory(source.Id));
        await File.WriteAllTextAsync(
            Path.Combine(sourceRoot, "instance.json"),
            JsonSerializer.Serialize(source, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true
            }));
        await File.WriteAllTextAsync(
            Path.Combine(paths.GetInstanceGameDirectory(source.Id), "fabric-marker.txt"),
            "fabric");

        var clone = await lifecycle.CloneAsync(
            source,
            "Fabric metadata clone",
            includeWorlds: false);

        Require(clone.Id != source.Id, "Fabric clone should receive a new id.");
        Require(clone.VersionId == source.VersionId, "Fabric clone should retain VersionId.");
        Require(clone.Loader == "fabric", "Fabric clone should retain loader.");
        Require(clone.BaseVersionId == "1.21.4", "Fabric clone should retain BaseVersionId.");
        Require(clone.LoaderVersion == "0.16.9", "Fabric clone should retain LoaderVersion.");

        var reloaded = (await new InstanceStoreService(paths).GetAllAsync())
            .Single(item => item.Id == clone.Id);
        Require(reloaded.BaseVersionId == "1.21.4",
            "Reloaded Fabric clone should retain BaseVersionId.");
        Require(reloaded.LoaderVersion == "0.16.9",
            "Reloaded Fabric clone should retain LoaderVersion.");
    }

    private static async Task<GameInstance> TestRenameAsync(
        InstanceLifecycleService lifecycle,
        InstanceStoreService store,
        NexoPathService paths,
        GameInstance source)
    {
        var renamed =
            await lifecycle.RenameAsync(
                source,
                "Renamed Source");

        Require(
            renamed.Id == source.Id,
            "Rename must preserve the instance id.");
        Require(
            renamed.Name == "Renamed Source",
            "Rename did not apply the requested name.");
        Require(
            renamed.VersionId == source.VersionId
            && renamed.Loader == source.Loader,
            "Rename must preserve version and loader metadata.");

        var reloaded =
            (await store.GetAllAsync())
                .Single(item =>
                    item.Id == source.Id);
        Require(
            reloaded.Name == "Renamed Source",
            "Renamed instance did not persist through the store.");

        var statePath =
            Path.Combine(
                paths.GetInstanceDirectory(source.Id),
                "install-state.json");
        using var state =
            JsonDocument.Parse(
                await File.ReadAllTextAsync(
                    statePath));
        var stateRoot =
            state.RootElement;
        var stateName =
            stateRoot.TryGetProperty("Name", out var upper)
                ? upper.GetString()
                : stateRoot.TryGetProperty("name", out var lower)
                    ? lower.GetString()
                    : null;
        Require(
            stateName == "Renamed Source",
            "Rename must synchronize install-state name metadata.");

        return renamed;
    }

    private static async Task TestDuplicateRenameRejectedAsync(
        InstanceLifecycleService lifecycle,
        InstanceStoreService store,
        GameInstance source)
    {
        var duplicate =
            await store.CreateAsync(
                "Duplicate Target",
                "1.21.4");

        try
        {
            await lifecycle.RenameAsync(
                source,
                "duplicate target");
            throw new Exception(
                "Case-insensitive duplicate rename unexpectedly succeeded.");
        }
        catch (InvalidOperationException)
        {
        }

        var reloaded =
            (await store.GetAllAsync())
                .Single(item =>
                    item.Id == source.Id);
        Require(
            reloaded.Name == source.Name,
            "Rejected duplicate rename must preserve the original name.");

        await lifecycle.DeleteAsync(
            duplicate);
    }

    private static async Task TestDeletePreservesBackupsAsync(
        InstanceLifecycleService lifecycle,
        InstanceStoreService store,
        NexoPathService paths)
    {
        var instance =
            await CreateDeleteFixtureAsync(
                store,
                paths,
                "Delete preserve backups");
        var backup =
            await lifecycle.CreateWorldBackupAsync(
                instance,
                "manual");

        await lifecycle.DeleteAsync(
            instance);

        Require(
            !Directory.Exists(
                paths.GetInstanceDirectory(
                    instance.Id)),
            "Delete should remove the canonical instance directory.");
        Require(
            File.Exists(backup.FilePath),
            "Delete should preserve backups by default.");
        Require(
            !(await store.GetAllAsync())
                .Any(item =>
                    item.Id == instance.Id),
            "Deleted instance must disappear from the instance store.");
        Require(
            !Directory.EnumerateDirectories(
                    paths.GetInstancesRoot(),
                    $".{instance.Id}.deleting-*",
                    SearchOption.TopDirectoryOnly)
                .Any(),
            "Successful delete should not leave an instance tombstone.");

        try
        {
            Directory.Delete(
                Path.GetDirectoryName(backup.FilePath)!,
                recursive: true);
        }
        catch
        {
        }
    }

    private static async Task TestDeleteRemovesBackupsAsync(
        InstanceLifecycleService lifecycle,
        InstanceStoreService store,
        NexoPathService paths)
    {
        var instance =
            await CreateDeleteFixtureAsync(
                store,
                paths,
                "Delete backups");
        var backup =
            await lifecycle.CreateWorldBackupAsync(
                instance,
                "manual");
        var backupRoot =
            Path.GetDirectoryName(
                backup.FilePath)!;

        await lifecycle.DeleteAsync(
            instance,
            deleteBackups: true);

        Require(
            !Directory.Exists(
                paths.GetInstanceDirectory(
                    instance.Id)),
            "Delete with backups should remove the canonical instance.");
        Require(
            !Directory.Exists(backupRoot),
            "Delete with backups should remove the physical instance backup root.");
    }

    private static async Task TestDeleteDoesNotFollowNestedLinkAsync(
        InstanceLifecycleService lifecycle,
        InstanceStoreService store,
        NexoPathService paths)
    {
        var instance =
            await store.CreateAsync(
                "Delete linked tree",
                "1.21.4");
        var gameRoot =
            paths.GetInstanceGameDirectory(
                instance.Id);
        Directory.CreateDirectory(gameRoot);

        var externalRoot =
            Path.Combine(
                Path.GetTempPath(),
                "un-nexo-delete-external-"
                + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(externalRoot);
        var sentinel =
            Path.Combine(
                externalRoot,
                "keep.txt");
        await File.WriteAllTextAsync(
            sentinel,
            "keep");

        var link =
            Path.Combine(
                gameRoot,
                "external-link");
        var linked = false;
        try
        {
            linked =
                TryCreateDirectoryLink(
                    link,
                    externalRoot);
            if (!linked)
            {
                await lifecycle.DeleteAsync(
                    instance);
                Console.WriteLine(
                    "SKIP instance delete nested-link regression: platform denied symlink creation");
                return;
            }

            await lifecycle.DeleteAsync(
                instance);

            Require(
                File.Exists(sentinel),
                "Instance delete must not follow a nested directory link.");
            Require(
                await File.ReadAllTextAsync(sentinel)
                == "keep",
                "Instance delete changed data behind a nested directory link.");
        }
        finally
        {
            if (linked)
            {
                TryDeleteDirectoryLink(
                    link);
            }
            try
            {
                if (Directory.Exists(
                        paths.GetInstanceDirectory(
                            instance.Id)))
                {
                    Directory.Delete(
                        paths.GetInstanceDirectory(
                            instance.Id),
                        recursive: true);
                }
            }
            catch
            {
            }
            try
            {
                Directory.Delete(
                    externalRoot,
                    recursive: true);
            }
            catch
            {
            }
        }
    }

    private static async Task TestDeleteRejectsLinkedBackupRootAsync(
        InstanceLifecycleService lifecycle,
        InstanceStoreService store,
        NexoPathService paths)
    {
        var instance =
            await store.CreateAsync(
                "Delete linked backup root",
                "1.21.4");
        var backupsRoot =
            Path.Combine(
                paths.GetDataRoot(),
                "backups");
        Directory.CreateDirectory(backupsRoot);

        var externalRoot =
            Path.Combine(
                Path.GetTempPath(),
                "un-nexo-delete-backup-external-"
                + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(externalRoot);
        var sentinel =
            Path.Combine(
                externalRoot,
                "keep.txt");
        await File.WriteAllTextAsync(
            sentinel,
            "keep");

        var linkedBackupRoot =
            Path.Combine(
                backupsRoot,
                instance.Id);
        var linked = false;
        try
        {
            linked =
                TryCreateDirectoryLink(
                    linkedBackupRoot,
                    externalRoot);
            if (!linked)
            {
                await lifecycle.DeleteAsync(
                    instance);
                Console.WriteLine(
                    "SKIP linked backup-root delete regression: platform denied symlink creation");
                return;
            }

            try
            {
                await lifecycle.DeleteAsync(
                    instance,
                    deleteBackups: true);
                throw new Exception(
                    "Deleting through a linked instance backup root should fail closed.");
            }
            catch (InvalidDataException)
            {
            }

            Require(
                Directory.Exists(
                    paths.GetInstanceDirectory(
                        instance.Id)),
                "Rejecting a linked backup root must leave the canonical instance intact.");
            Require(
                File.Exists(sentinel)
                && await File.ReadAllTextAsync(sentinel)
                    == "keep",
                "Rejecting a linked backup root must not modify its external target.");
        }
        finally
        {
            if (linked
                && Directory.Exists(linkedBackupRoot))
            {
                try
                {
                    Directory.Delete(linkedBackupRoot);
                }
                catch
                {
                }
            }

            try
            {
                if (Directory.Exists(
                        paths.GetInstanceDirectory(
                            instance.Id)))
                {
                    await lifecycle.DeleteAsync(
                        instance);
                }
            }
            catch
            {
            }

            try
            {
                Directory.Delete(
                    externalRoot,
                    recursive: true);
            }
            catch
            {
            }
        }
    }

    private static async Task TestCancelledDeletePreservesInstanceAsync(
        InstanceLifecycleService lifecycle,
        InstanceStoreService store,
        NexoPathService paths)
    {
        var instance =
            await store.CreateAsync(
                "Cancelled delete",
                "1.21.4");
        var root =
            paths.GetInstanceDirectory(
                instance.Id);

        using var cancellation =
            new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            await lifecycle.DeleteAsync(
                instance,
                cancellationToken:
                    cancellation.Token);
            throw new Exception(
                "Pre-cancelled instance delete unexpectedly succeeded.");
        }
        catch (OperationCanceledException)
        {
        }

        Require(
            Directory.Exists(root),
            "Pre-cancelled delete must leave the canonical instance intact.");

        await lifecycle.DeleteAsync(
            instance);
    }

    private static async Task<GameInstance> CreateDeleteFixtureAsync(
        InstanceStoreService store,
        NexoPathService paths,
        string name)
    {
        var instance =
            await store.CreateAsync(
                name,
                "1.21.4");
        var world =
            Path.Combine(
                paths.GetInstanceGameDirectory(
                    instance.Id),
                "saves",
                "World");
        Directory.CreateDirectory(world);
        await File.WriteAllTextAsync(
            Path.Combine(
                world,
                "level.dat"),
            name);
        return instance;
    }

    private static bool TryCreateDirectoryLink(
        string linkPath,
        string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(
                linkPath,
                targetPath);
            return true;
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException
            or IOException
            or PlatformNotSupportedException
            or NotSupportedException)
        {
            return false;
        }
    }

    private static void TryDeleteDirectoryLink(
        string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path);
        }
        catch
        {
        }
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

    private static async Task TestIncompleteRestorePreservesCurrentWorldAsync(
        InstanceLifecycleService lifecycle, NexoPathService paths,
        GameInstance source, WorldBackupInfo goodBackup)
    {
        var world = goodBackup.Worlds.Single(item => item.Name == "World A");
        var level = Path.Combine(paths.GetInstanceGameDirectory(source.Id), "saves", world.Name, "level.dat");
        foreach (var mode in new[] { "missing-file", "wrong-size" })
        {
            await File.WriteAllTextAsync(level, "current-world-must-survive");
            var damagedPath = Path.Combine(Path.GetDirectoryName(goodBackup.FilePath)!, mode + ".zip");
            File.Copy(goodBackup.FilePath, damagedPath);
            using (var archive = ZipFile.Open(damagedPath, ZipArchiveMode.Update))
            {
                var entryName = world.ArchivePrefix + "/region.mca";
                archive.GetEntry(entryName)!.Delete();
                if (mode == "wrong-size")
                {
                    var entry = archive.CreateEntry(entryName);
                    await using var writer = new StreamWriter(entry.Open());
                    await writer.WriteAsync("x");
                }
            }
            try
            {
                await lifecycle.RestoreWorldAsync(source, goodBackup with { FilePath = damagedPath }, world.Name);
                throw new InvalidOperationException(mode + " backup unexpectedly restored.");
            }
            catch (InvalidDataException) { }
            Require(await File.ReadAllTextAsync(level) == "current-world-must-survive",
                "Incomplete backup must leave the active world untouched.");
            var staging = Path.Combine(paths.GetInstanceDirectory(source.Id), ".restore-staging");
            Require(!Directory.Exists(staging) || !Directory.EnumerateFileSystemEntries(staging).Any(),
                "Rejected restore must clean its staging directory.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
