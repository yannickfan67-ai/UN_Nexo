using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed class InstanceLifecycleService
{
    private const int BackupSchema = 1;
    private const long FreeSpaceReserveBytes = 64L * 1024 * 1024;
    private readonly NexoPathService _paths;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public InstanceLifecycleService(NexoPathService paths)
    {
        _paths = paths;
    }

    public async Task<GameInstance> CloneAsync(
        GameInstance source,
        string newName,
        bool includeWorlds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        _paths.EnsureDirectories();

        var normalizedName = NormalizeInstanceName(newName);
        var existing = await new InstanceStoreService(_paths).GetAllAsync(cancellationToken);
        if (existing.Any(item => item.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"An instance named '{normalizedName}' already exists.");

        var sourceRoot = _paths.GetInstanceDirectory(source.Id);
        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException($"Instance directory is missing: {sourceRoot}");

        var newId = Guid.NewGuid().ToString("N");
        var destinationRoot = _paths.GetInstanceDirectory(newId);
        if (Directory.Exists(destinationRoot))
            throw new IOException("The clone destination already exists.");

        var stagingParent = Path.Combine(_paths.GetDataRoot(), ".staging", "clones");
        Directory.CreateDirectory(stagingParent);
        var stagingRoot = Path.Combine(stagingParent, newId + ".tmp");
        if (Directory.Exists(stagingRoot))
            Directory.Delete(stagingRoot, recursive: true);
        Directory.CreateDirectory(stagingRoot);

        try
        {
            var estimatedBytes = MeasureDirectory(sourceRoot, path => ShouldCopyClonePath(sourceRoot, path, includeWorlds));
            EnsureFreeSpace(stagingParent, estimatedBytes + FreeSpaceReserveBytes);

            await CopyDirectoryAsync(
                sourceRoot,
                stagingRoot,
                path => ShouldCopyClonePath(sourceRoot, path, includeWorlds),
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            var clone = new GameInstance(
                newId,
                normalizedName,
                source.VersionId,
                source.Loader,
                DateTimeOffset.UtcNow);

            await WriteJsonAtomicAsync(Path.Combine(stagingRoot, "instance.json"), clone, cancellationToken);
            await RewriteInstallStateAsync(stagingRoot, clone, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(stagingRoot, destinationRoot);
            return clone;
        }
        catch
        {
            TryDeleteDirectory(stagingRoot);
            throw;
        }
    }

    public async Task<WorldBackupInfo> CreateWorldBackupAsync(
        GameInstance instance,
        string kind = "manual",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        cancellationToken.ThrowIfCancellationRequested();
        _paths.EnsureDirectories();

        var normalizedKind = NormalizeBackupKind(kind);
        var savesRoot = Path.Combine(_paths.GetInstanceGameDirectory(instance.Id), "saves");
        if (!Directory.Exists(savesRoot))
            throw new InvalidOperationException("This instance has no saves directory yet.");

        var worlds = Directory.EnumerateDirectories(savesRoot)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (worlds.Length == 0)
            throw new InvalidOperationException("This instance has no worlds to back up.");

        foreach (var world in worlds)
            RejectReparsePoint(world);

        var totalBytes = worlds.Sum(world => MeasureDirectory(world, _ => true));
        var backupRoot = GetBackupRoot(instance.Id);
        Directory.CreateDirectory(backupRoot);
        EnsureFreeSpace(backupRoot, totalBytes + FreeSpaceReserveBytes);

        var createdAt = DateTimeOffset.UtcNow;
        var backupId = Guid.NewGuid().ToString("N");
        var fileName = $"{createdAt:yyyyMMdd-HHmmssfff}-{normalizedKind}-{backupId[..8]}.zip";
        var finalPath = Path.Combine(backupRoot, fileName);
        var tempPath = finalPath + ".tmp";
        var worldEntries = new List<WorldBackupWorld>(worlds.Length);

        try
        {
            await using (var file = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.ReadWrite,
                             FileShare.None,
                             128 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
            {
                for (var index = 0; index < worlds.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var worldPath = worlds[index];
                    var worldName = Path.GetFileName(worldPath);
                    var prefix = $"worlds/{index:D4}";
                    var (bytes, count) = await AddDirectoryToArchiveAsync(
                        archive,
                        worldPath,
                        prefix,
                        cancellationToken);
                    worldEntries.Add(new WorldBackupWorld(worldName, prefix, bytes, count));
                }

                var manifest = new BackupManifest(
                    BackupSchema,
                    backupId,
                    instance.Id,
                    instance.Name,
                    normalizedKind,
                    createdAt,
                    worldEntries);
                var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Fastest);
                await using var manifestStream = manifestEntry.Open();
                await JsonSerializer.SerializeAsync(manifestStream, manifest, _json, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, finalPath);
            var archiveBytes = new FileInfo(finalPath).Length;
            return new WorldBackupInfo(
                backupId,
                instance.Id,
                instance.Name,
                normalizedKind,
                createdAt,
                finalPath,
                archiveBytes,
                worldEntries);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }
    }

    public async Task<IReadOnlyList<WorldBackupInfo>> GetBackupsAsync(
        GameInstance instance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var root = GetBackupRoot(instance.Id);
        if (!Directory.Exists(root))
            return [];

        var result = new List<WorldBackupInfo>();
        foreach (var path in Directory.EnumerateFiles(root, "*.zip", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = await ReadBackupAsync(path, cancellationToken);
                if (info.InstanceId.Equals(instance.Id, StringComparison.Ordinal))
                    result.Add(info);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException)
            {
                // Corrupt/incomplete backups are ignored instead of breaking the entire list.
            }
        }

        return result
            .OrderByDescending(item => item.CreatedAt)
            .ToArray();
    }

    public async Task<WorldRestoreResult> RestoreWorldAsync(
        GameInstance instance,
        WorldBackupInfo backup,
        string worldName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(backup);
        if (!backup.InstanceId.Equals(instance.Id, StringComparison.Ordinal))
            throw new InvalidOperationException("This backup belongs to a different instance.");

        var backupPath = Path.GetFullPath(backup.FilePath);
        var backupRoot = Path.GetFullPath(GetBackupRoot(instance.Id));
        EnsureContained(backupRoot, backupPath);
        if (!File.Exists(backupPath))
            throw new FileNotFoundException("The selected backup file no longer exists.", backupPath);

        var inspected = await ReadBackupAsync(backupPath, cancellationToken);
        var world = inspected.Worlds.FirstOrDefault(item => item.Name.Equals(worldName, StringComparison.Ordinal));
        if (world is null)
            throw new InvalidOperationException("The selected world is not present in this backup.");

        var instanceRoot = _paths.GetInstanceDirectory(instance.Id);
        var savesRoot = Path.Combine(_paths.GetInstanceGameDirectory(instance.Id), "saves");
        Directory.CreateDirectory(savesRoot);
        var destinationWorld = ResolveChild(savesRoot, world.Name);

        var stagingRoot = Path.Combine(instanceRoot, ".restore-staging", Guid.NewGuid().ToString("N"));
        var stagedWorld = Path.Combine(stagingRoot, "world");
        Directory.CreateDirectory(stagedWorld);
        EnsureFreeSpace(instanceRoot, world.UncompressedBytes + FreeSpaceReserveBytes);

        try
        {
            using var archive = ZipFile.OpenRead(backupPath);
            var prefix = world.ArchivePrefix.TrimEnd('/') + "/";
            var extractedAny = false;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                if (IsZipSymlink(entry))
                    throw new InvalidDataException("Backup contains a symbolic link, which is not allowed.");

                var relative = entry.FullName[prefix.Length..];
                if (relative.Length == 0)
                    continue;

                var target = ResolveChild(stagedWorld, relative.Replace('/', Path.DirectorySeparatorChar));
                if (entry.FullName.EndsWith('/', StringComparison.Ordinal))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var input = entry.Open();
                await using var output = new FileStream(
                    target,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await input.CopyToAsync(output, cancellationToken);
                extractedAny = true;
            }

            if (!extractedAny && world.FileCount > 0)
                throw new InvalidDataException("The backup does not contain the expected world files.");

            cancellationToken.ThrowIfCancellationRequested();

            string? safetyCopy = null;
            if (Directory.Exists(destinationWorld))
            {
                var safetyRoot = Path.Combine(
                    instanceRoot,
                    "restore-safety",
                    $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}");
                Directory.CreateDirectory(safetyRoot);
                safetyCopy = ResolveChild(safetyRoot, world.Name);
                Directory.Move(destinationWorld, safetyCopy);
            }

            try
            {
                Directory.Move(stagedWorld, destinationWorld);
            }
            catch
            {
                if (safetyCopy is not null && Directory.Exists(safetyCopy) && !Directory.Exists(destinationWorld))
                    Directory.Move(safetyCopy, destinationWorld);
                throw;
            }

            return new WorldRestoreResult(world.Name, safetyCopy, DateTimeOffset.UtcNow);
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
        }
    }

    private async Task<WorldBackupInfo> ReadBackupAsync(string path, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("manifest.json")
            ?? throw new InvalidDataException("Backup manifest is missing.");
        await using var stream = entry.Open();
        var manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(stream, _json, cancellationToken)
            ?? throw new InvalidDataException("Backup manifest is invalid.");
        if (manifest.Schema != BackupSchema)
            throw new InvalidDataException($"Unsupported backup schema {manifest.Schema}.");
        if (string.IsNullOrWhiteSpace(manifest.Id)
            || string.IsNullOrWhiteSpace(manifest.InstanceId)
            || manifest.Worlds is null)
            throw new InvalidDataException("Backup manifest is incomplete.");

        return new WorldBackupInfo(
            manifest.Id,
            manifest.InstanceId,
            manifest.InstanceName,
            manifest.Kind,
            manifest.CreatedAt,
            path,
            new FileInfo(path).Length,
            manifest.Worlds);
    }

    private async Task<(long Bytes, int Files)> AddDirectoryToArchiveAsync(
        ZipArchive archive,
        string sourceRoot,
        string archivePrefix,
        CancellationToken cancellationToken)
    {
        long totalBytes = 0;
        var fileCount = 0;
        foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
            RejectReparsePoint(directory);

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectReparsePoint(file);
            var relative = Path.GetRelativePath(sourceRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Contains("../", StringComparison.Ordinal) || relative.StartsWith("..", StringComparison.Ordinal))
                throw new InvalidDataException("A world file escaped its save directory.");

            var info = new FileInfo(file);
            totalBytes += info.Length;
            fileCount++;
            var entry = archive.CreateEntry($"{archivePrefix}/{relative}", CompressionLevel.Optimal);
            await using var input = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = entry.Open();
            await input.CopyToAsync(output, cancellationToken);
        }

        return (totalBytes, fileCount);
    }

    private async Task CopyDirectoryAsync(
        string sourceRoot,
        string destinationRoot,
        Func<string, bool> shouldCopy,
        CancellationToken cancellationToken)
    {
        foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!shouldCopy(directory))
                continue;
            RejectReparsePoint(directory);
            var relative = Path.GetRelativePath(sourceRoot, directory);
            Directory.CreateDirectory(ResolveChild(destinationRoot, relative));
        }

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!shouldCopy(file))
                continue;
            RejectReparsePoint(file);
            var relative = Path.GetRelativePath(sourceRoot, file);
            var target = ResolveChild(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(
                target,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, cancellationToken);
        }
    }

    private static bool ShouldCopyClonePath(string sourceRoot, string path, bool includeWorlds)
    {
        var relative = Path.GetRelativePath(sourceRoot, path);
        if (relative.Equals("instance.json", StringComparison.OrdinalIgnoreCase))
            return false;
        if (relative.Equals("restore-safety", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith($"restore-safety{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            || relative.Equals(".restore-staging", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith($".restore-staging{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!includeWorlds
            && (relative.Equals(Path.Combine("game", "saves"), StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith(Path.Combine("game", "saves") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            return false;
        return true;
    }

    private async Task RewriteInstallStateAsync(
        string instanceRoot,
        GameInstance clone,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(instanceRoot, "install-state.json");
        if (!File.Exists(path))
            return;

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            var node = JsonNode.Parse(json)?.AsObject();
            if (node is null)
                return;
            node["id"] = clone.Id;
            node["name"] = clone.Name;
            await File.WriteAllTextAsync(path, node.ToJsonString(_json), cancellationToken);
        }
        catch (JsonException)
        {
            // A stale state file should not make an otherwise valid clone fail.
        }
    }

    private async Task WriteJsonAtomicAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var temp = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(value, _json), cancellationToken);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDeleteFile(temp);
            throw;
        }
    }

    private string GetBackupRoot(string instanceId)
        => Path.Combine(_paths.GetDataRoot(), "backups", instanceId);

    private static string NormalizeInstanceName(string value)
    {
        var name = (value ?? string.Empty).Trim();
        if (name.Length is < 1 or > 80 || name.Any(char.IsControl))
            throw new ArgumentException("Instance name must be 1-80 printable characters.", nameof(value));
        return name;
    }

    private static string NormalizeBackupKind(string value)
    {
        var kind = (value ?? string.Empty).Trim().ToLowerInvariant();
        return kind switch
        {
            "manual" => "manual",
            "pre-upgrade" => "pre-upgrade",
            _ => throw new ArgumentException("Backup kind must be manual or pre-upgrade.", nameof(value))
        };
    }

    private static long MeasureDirectory(string root, Func<string, bool> shouldCount)
    {
        long total = 0;
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            if (shouldCount(directory))
                RejectReparsePoint(directory);
        }
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (!shouldCount(file))
                continue;
            RejectReparsePoint(file);
            total = checked(total + new FileInfo(file).Length);
        }
        return total;
    }

    private static void EnsureFreeSpace(string path, long requiredBytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(root))
                return;
            var drive = new DriveInfo(root);
            if (drive.IsReady && drive.AvailableFreeSpace < requiredBytes)
                throw new IOException(
                    $"Not enough free space. Need about {requiredBytes / (1024 * 1024)} MiB, " +
                    $"available {drive.AvailableFreeSpace / (1024 * 1024)} MiB.");
        }
        catch (ArgumentException)
        {
            // Some virtual filesystems do not expose DriveInfo. Atomic staging still protects originals.
        }
    }

    private static string ResolveChild(string root, string relative)
    {
        var rootFull = Path.GetFullPath(root);
        var childFull = Path.GetFullPath(Path.Combine(rootFull, relative));
        EnsureContained(rootFull, childFull);
        return childFull;
    }

    private static void EnsureContained(string root, string child)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var childFull = Path.GetFullPath(child);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!childFull.StartsWith(rootFull, comparison))
            throw new InvalidDataException("A path escaped the expected directory.");
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Symbolic links/reparse points are not copied: {path}");
    }

    private static bool IsZipSymlink(ZipArchiveEntry entry)
    {
        var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
        return unixMode == 0xA000;
    }

    private static void TryDeleteDirectory(string path)
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

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private sealed record BackupManifest(
        int Schema,
        string Id,
        string InstanceId,
        string InstanceName,
        string Kind,
        DateTimeOffset CreatedAt,
        IReadOnlyList<WorldBackupWorld> Worlds);
}
