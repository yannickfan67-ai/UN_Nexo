using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.InstanceLifecycle.Tests;

internal static class BackupIntegrityRegression
{
    private const int ManifestLimit = 512 * 1024;

    internal static async Task RunAsync(
        InstanceLifecycleService lifecycle,
        NexoPathService paths,
        GameInstance source,
        WorldBackupInfo backup)
    {
        await TestSchema2ManifestAsync(backup);
        await TestSameSizeTamperAsync(lifecycle, paths, source, backup);
        await TestManifestValidationAsync(lifecycle, paths, source, backup);
        await TestLegacySchema1RestoreAsync(lifecycle, paths, source, backup);
    }

    private static async Task TestSchema2ManifestAsync(WorldBackupInfo backup)
    {
        Require(backup.Schema == 2, "New backups must use schema 2.");
        Require(backup.Worlds.Count > 0, "Schema-2 backup should contain worlds.");

        using var archive = ZipFile.OpenRead(backup.FilePath);
        var manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new InvalidOperationException("Schema-2 backup manifest is missing.");
        Require(manifestEntry.Length <= ManifestLimit,
            "Nexo-generated backup manifest should fit the bounded metadata policy.");

        await using (var stream = manifestEntry.Open())
        using (var manifest = await JsonDocument.ParseAsync(stream))
        {
            Require(manifest.RootElement.GetProperty("schema").GetInt32() == 2,
                "Published manifest should declare schema 2.");
        }

        foreach (var world in backup.Worlds)
        {
            Require(world.Files is not null,
                $"Schema-2 world '{world.Name}' must contain per-file integrity metadata.");
            Require(world.Files!.Count == world.FileCount,
                $"Schema-2 world '{world.Name}' file metadata count must match FileCount.");

            long archivedBytes = 0;
            var archivedFiles = 0;
            foreach (var file in world.Files)
            {
                var entryName = world.ArchivePrefix.TrimEnd('/') + "/" + file.Path;
                var entry = archive.GetEntry(entryName)
                    ?? throw new InvalidOperationException(
                        $"Schema-2 backup is missing archive entry '{entryName}'.");
                Require(entry.Length == file.Size,
                    $"Manifest size for '{entryName}' must describe archived bytes.");

                await using var input = entry.Open();
                var digest = Convert.ToHexString(
                    await SHA256.HashDataAsync(input)).ToLowerInvariant();
                Require(
                    string.Equals(digest, file.Sha256, StringComparison.OrdinalIgnoreCase),
                    $"Manifest SHA-256 for '{entryName}' must match archived bytes.");

                archivedBytes = checked(archivedBytes + entry.Length);
                archivedFiles++;
            }

            Require(archivedFiles == world.FileCount,
                $"Schema-2 world '{world.Name}' file count must describe the archive.");
            Require(archivedBytes == world.UncompressedBytes,
                $"Schema-2 world '{world.Name}' byte total must describe the archive.");
        }
    }

    private static async Task TestSameSizeTamperAsync(
        InstanceLifecycleService lifecycle,
        NexoPathService paths,
        GameInstance source,
        WorldBackupInfo backup)
    {
        var world = backup.Worlds.Single(item => item.Name == "World A");
        var file = world.Files?.Single(item => item.Path == "level.dat")
            ?? throw new InvalidOperationException(
                "Schema-2 fixture should contain World A/level.dat integrity metadata.");
        Require(file.Size > 0 && file.Size <= int.MaxValue,
            "Same-size tamper fixture requires a small non-empty file.");

        var tamperedPath = Path.Combine(
            Path.GetDirectoryName(backup.FilePath)!,
            "same-size-tamper.zip");
        File.Copy(backup.FilePath, tamperedPath, overwrite: true);
        using (var archive = ZipFile.Open(tamperedPath, ZipArchiveMode.Update))
        {
            var entryName = world.ArchivePrefix.TrimEnd('/') + "/" + file.Path;
            var original = archive.GetEntry(entryName)
                ?? throw new InvalidOperationException("Tamper fixture entry is missing.");
            original.Delete();

            var replacement = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            await using var output = replacement.Open();
            var bytes = Enumerable.Repeat((byte)'X', checked((int)file.Size)).ToArray();
            await output.WriteAsync(bytes);
        }

        var activeLevel = Path.Combine(
            paths.GetInstanceGameDirectory(source.Id),
            "saves",
            world.Name,
            "level.dat");
        await File.WriteAllTextAsync(activeLevel, "must-survive-same-size-tamper");

        try
        {
            await lifecycle.RestoreWorldAsync(
                source,
                backup with { FilePath = tamperedPath },
                world.Name);
            throw new InvalidOperationException(
                "Same-size content tampering unexpectedly restored.");
        }
        catch (InvalidDataException)
        {
        }

        Require(
            await File.ReadAllTextAsync(activeLevel) == "must-survive-same-size-tamper",
            "SHA-256 rejection must happen before replacing the active world.");
        RequireRestoreStagingClean(paths, source);
    }

    private static async Task TestManifestValidationAsync(
        InstanceLifecycleService lifecycle,
        NexoPathService paths,
        GameInstance source,
        WorldBackupInfo goodBackup)
    {
        var backupRoot = Path.GetDirectoryName(goodBackup.FilePath)!;

        var nullWorldPath = Path.Combine(backupRoot, "null-world-manifest.zip");
        await CreateManifestOnlyArchiveAsync(
            nullWorldPath,
            "{"
            + "\"schema\":1,"
            + "\"id\":\"null-world\","
            + "\"instanceId\":\"" + source.Id + "\","
            + "\"instanceName\":\"" + Escape(source.Name) + "\","
            + "\"kind\":\"manual\","
            + "\"createdAt\":\"2026-01-01T00:00:00Z\","
            + "\"worlds\":[null]"
            + "}");

        var nearLimitPath = Path.Combine(backupRoot, "near-limit-manifest.zip");
        const string nearId = "near-limit";
        var validPrefix =
            "{\"schema\":1,\"id\":\"" + nearId + "\","
            + "\"instanceId\":\"" + source.Id + "\","
            + "\"instanceName\":\"" + Escape(source.Name) + "\","
            + "\"kind\":\"manual\","
            + "\"createdAt\":\"2026-01-01T00:00:00Z\","
            + "\"worlds\":[{\"name\":\"Sized World\","
            + "\"archivePrefix\":\"worlds/0000\","
            + "\"uncompressedBytes\":0,\"fileCount\":0}],"
            + "\"padding\":\"";
        const string suffix = "\"}";
        var paddingLength = ManifestLimit - 1
            - Encoding.UTF8.GetByteCount(validPrefix)
            - Encoding.UTF8.GetByteCount(suffix);
        Require(paddingLength > 0, "Near-limit manifest fixture has invalid padding.");
        var nearLimitJson = validPrefix + new string('a', paddingLength) + suffix;
        Require(Encoding.UTF8.GetByteCount(nearLimitJson) == ManifestLimit - 1,
            "Near-limit manifest fixture must sit one byte below the limit.");
        await CreateManifestOnlyArchiveAsync(nearLimitPath, nearLimitJson);

        var oversizedPath = Path.Combine(backupRoot, "oversized-manifest.zip");
        var oversizedPadding = paddingLength + 3;
        var oversizedJson = validPrefix.Replace(
                "\"id\":\"" + nearId + "\"",
                "\"id\":\"oversized\"",
                StringComparison.Ordinal)
            + new string('b', oversizedPadding)
            + suffix;
        Require(Encoding.UTF8.GetByteCount(oversizedJson) > ManifestLimit,
            "Oversized manifest fixture must cross the limit.");
        await CreateManifestOnlyArchiveAsync(oversizedPath, oversizedJson);

        var listed = await lifecycle.GetBackupsAsync(source);
        Require(listed.Any(item => item.Id == goodBackup.Id),
            "Healthy sibling backup must remain listed.");
        Require(listed.Any(item => item.Id == nearId),
            "Just-under-limit manifest should remain listable.");
        Require(listed.All(item => item.Id != "null-world"),
            "Manifest containing a null world must be skipped.");
        Require(listed.All(item => item.Id != "oversized"),
            "Oversized manifest must be skipped.");

        foreach (var invalid in new[]
        {
            (Path: nullWorldPath, Id: "null-world"),
            (Path: oversizedPath, Id: "oversized")
        })
        {
            var candidate = new WorldBackupInfo(
                invalid.Id,
                source.Id,
                source.Name,
                "manual",
                DateTimeOffset.UtcNow,
                invalid.Path,
                new FileInfo(invalid.Path).Length,
                []);

            try
            {
                await lifecycle.RestoreWorldAsync(
                    source,
                    candidate,
                    "World A");
                throw new InvalidOperationException(
                    $"Invalid manifest '{invalid.Id}' unexpectedly reached restore.");
            }
            catch (InvalidDataException)
            {
            }

            RequireRestoreStagingClean(paths, source);
        }
    }

    private static async Task TestLegacySchema1RestoreAsync(
        InstanceLifecycleService lifecycle,
        NexoPathService paths,
        GameInstance source,
        WorldBackupInfo goodBackup)
    {
        var backupRoot = Path.GetDirectoryName(goodBackup.FilePath)!;
        var legacyPath = Path.Combine(backupRoot, "legacy-schema1.zip");
        var payload = Encoding.UTF8.GetBytes("legacy-schema1-data");
        const string worldName = "Legacy World";
        const string prefix = "worlds/0000";

        using (var archive = ZipFile.Open(legacyPath, ZipArchiveMode.Create))
        {
            var manifest = archive.CreateEntry("manifest.json", CompressionLevel.Fastest);
            await using (var output = manifest.Open())
            {
                var json =
                    "{\"schema\":1,\"id\":\"legacy-schema1\","
                    + "\"instanceId\":\"" + source.Id + "\","
                    + "\"instanceName\":\"" + Escape(source.Name) + "\","
                    + "\"kind\":\"manual\","
                    + "\"createdAt\":\"2026-01-01T00:00:00Z\","
                    + "\"worlds\":[{\"name\":\"" + worldName + "\","
                    + "\"archivePrefix\":\"" + prefix + "\","
                    + "\"uncompressedBytes\":" + payload.Length + ","
                    + "\"fileCount\":1}]}";
                var bytes = Encoding.UTF8.GetBytes(json);
                await output.WriteAsync(bytes);
            }

            var entry = archive.CreateEntry(prefix + "/level.dat", CompressionLevel.Optimal);
            await using var entryOutput = entry.Open();
            await entryOutput.WriteAsync(payload);
        }

        var legacy = new WorldBackupInfo(
            "legacy-schema1",
            source.Id,
            source.Name,
            "manual",
            DateTimeOffset.UtcNow,
            legacyPath,
            new FileInfo(legacyPath).Length,
            [new WorldBackupWorld(worldName, prefix, payload.Length, 1)]);

        await lifecycle.RestoreWorldAsync(source, legacy, worldName);
        var restored = Path.Combine(
            paths.GetInstanceGameDirectory(source.Id),
            "saves",
            worldName,
            "level.dat");
        Require(File.Exists(restored),
            "Supported schema-1 backup should still restore.");
        Require(
            (await File.ReadAllBytesAsync(restored)).SequenceEqual(payload),
            "Schema-1 compatibility restore should preserve legacy payload bytes.");
    }

    private static async Task CreateManifestOnlyArchiveAsync(
        string path,
        string manifestJson)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var manifest = archive.CreateEntry("manifest.json", CompressionLevel.Fastest);
        await using var output = manifest.Open();
        var bytes = Encoding.UTF8.GetBytes(manifestJson);
        await output.WriteAsync(bytes);
    }

    private static void RequireRestoreStagingClean(
        NexoPathService paths,
        GameInstance source)
    {
        var staging = Path.Combine(
            paths.GetInstanceDirectory(source.Id),
            ".restore-staging");
        Require(
            !Directory.Exists(staging)
            || !Directory.EnumerateFileSystemEntries(staging).Any(),
            "Rejected restore must not leave restore staging content.");
    }

    private static string Escape(string value)
        => JsonSerializer.Serialize(value)[1..^1];

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
