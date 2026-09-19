using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed class InstanceLifecycleService
{
    private const int LegacyBackupSchema = 1;
    private const int BackupSchema = 2;
    private const int MaxBackupManifestBytes = 512 * 1024;
    private const int MaxBackupWorlds = 4096;
    private const int MaxBackupWorldNameLength = 255;
    private const int MaxArchivePrefixLength = 512;
    private const int MaxBackupFilePathLength = 4096;
    private const long FreeSpaceReserveBytes = 64L * 1024 * 1024;
    private readonly NexoPathService _paths;
    private readonly InstanceOperationCoordinator _operations;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public InstanceLifecycleService(NexoPathService paths)
    {
        _paths = paths;
        _operations = new InstanceOperationCoordinator(paths);
    }

    public async Task<GameInstance> CloneAsync(
        GameInstance source,
        string newName,
        bool includeWorlds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        await using var operationLease = await _operations.AcquireAsync(
            source.Id,
            "clone-instance",
            cancellationToken);
        _paths.EnsureDirectories();

        var normalizedName = NormalizeInstanceName(newName);
        var existing = await new InstanceStoreService(_paths).GetAllAsync(cancellationToken);
        if (existing.Any(item => item.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"An instance named '{normalizedName}' already exists.");

        var sourceRoot = _paths.GetInstanceDirectory(source.Id);
        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException($"Instance directory is missing: {sourceRoot}");
        RejectReparsePoint(sourceRoot);

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
            var estimatedBytes = MeasureCloneBytes(sourceRoot, sourceRoot, includeWorlds);
            EnsureFreeSpace(stagingParent, estimatedBytes + FreeSpaceReserveBytes);

            await CopyCloneTreeAsync(
                sourceRoot,
                stagingRoot,
                sourceRoot,
                includeWorlds,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            var clone = new GameInstance(
                newId,
                normalizedName,
                source.VersionId,
                source.Loader,
                DateTimeOffset.UtcNow,
                source.BaseVersionId,
                source.LoaderVersion);

            await AtomicJsonFile.WriteAsync(
                Path.Combine(stagingRoot, "instance.json"),
                clone,
                _json,
                cancellationToken);
            await RewriteInstallStateAsync(stagingRoot, clone, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            _paths.EnsureInstancesRootPhysical();
            var finalDestinationRoot = _paths.GetInstanceDirectory(newId);
            if (Directory.Exists(finalDestinationRoot) || File.Exists(finalDestinationRoot))
                throw new IOException("The clone destination changed before publication.");
            _paths.EnsureInstancesRootPhysical();
            Directory.Move(stagingRoot, finalDestinationRoot);
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
        await using var operationLease = await _operations.AcquireAsync(
            instance.Id,
            "backup-worlds",
            cancellationToken);
        _paths.EnsureDirectories();

        var normalizedKind = NormalizeBackupKind(kind);
        var savesRoot = Path.Combine(_paths.GetInstanceGameDirectory(instance.Id), "saves");
        if (!Directory.Exists(savesRoot))
            throw new InvalidOperationException("This instance has no saves directory yet.");
        RejectReparsePoint(savesRoot);

        var worlds = Directory.EnumerateDirectories(savesRoot)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (worlds.Length == 0)
            throw new InvalidOperationException("This instance has no worlds to back up.");

        foreach (var world in worlds)
            RejectReparsePoint(world);

        var totalBytes = worlds.Sum(MeasureSafeDirectoryBytes);
        var backupRoot = EnsureBackupRootPhysical(instance.Id, create: true);
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
                    var (bytes, count, files) = await AddDirectoryToArchiveAsync(
                        archive,
                        worldPath,
                        prefix,
                        cancellationToken);
                    worldEntries.Add(new WorldBackupWorld(worldName, prefix, bytes, count)
                    {
                        Files = files
                    });
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
            backupRoot = EnsureBackupRootPhysical(instance.Id, create: false);
            ValidateBackupFilePhysical(
                backupRoot,
                tempPath,
                requireZipExtension: false);
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
                worldEntries)
            {
                Schema = BackupSchema
            };
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
        var root = EnsureBackupRootPhysical(instance.Id, create: false);
        if (!Directory.Exists(root))
            return [];

        var result = new List<WorldBackupInfo>();
        foreach (var path in Directory.EnumerateFiles(root, "*.zip", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = await ReadBackupAsync(root, path, cancellationToken);
                if (info.InstanceId.Equals(instance.Id, StringComparison.Ordinal))
                    result.Add(info);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException)
            {
                // Corrupt/incomplete backups are ignored instead of breaking the whole list.
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
        await using var operationLease = await _operations.AcquireAsync(
            instance.Id,
            "restore-world",
            cancellationToken);
        if (!backup.InstanceId.Equals(instance.Id, StringComparison.Ordinal))
            throw new InvalidOperationException("This backup belongs to a different instance.");

        var backupRoot = EnsureBackupRootPhysical(instance.Id, create: false);
        var backupPath = Path.GetFullPath(backup.FilePath);
        ValidateBackupFilePhysical(backupRoot, backupPath);

        var inspected = await ReadBackupAsync(backupRoot, backupPath, cancellationToken);
        if (!inspected.InstanceId.Equals(instance.Id, StringComparison.Ordinal))
            throw new InvalidOperationException("The backup file was replaced and now belongs to a different instance.");
        var world = inspected.Worlds.FirstOrDefault(
            item => item is not null && item.Name.Equals(worldName, StringComparison.Ordinal));
        if (world is null)
            throw new InvalidOperationException("The selected world is not present in this backup.");
        if (world.FileCount < 0 || world.UncompressedBytes < 0
            || world.UncompressedBytes > long.MaxValue - FreeSpaceReserveBytes)
            throw new InvalidDataException("The backup contains invalid world file totals.");

        Dictionary<string, WorldBackupFile>? expectedFiles = null;
        if (inspected.Schema == BackupSchema)
        {
            if (world.Files is null)
                throw new InvalidDataException("Backup file integrity metadata is missing.");
            expectedFiles = world.Files.ToDictionary(item => item.Path, StringComparer.Ordinal);
        }

        var instanceRoot = _paths.EnsureInstanceDirectoryPhysical(instance.Id);
        var savesRoot = EnsureRestoreSavesRootPhysical(instance.Id);
        var destinationWorld = ResolveChild(savesRoot, world.Name);

        var stagingParent = ResolveChild(instanceRoot, ".restore-staging");
        EnsurePhysicalDirectory(
            stagingParent,
            create: true,
            "Restore staging root");
        _paths.EnsureInstanceDirectoryPhysical(instance.Id);
        var stagingRoot = ResolveChild(
            stagingParent,
            Guid.NewGuid().ToString("N"));
        EnsurePhysicalDirectory(
            stagingRoot,
            create: true,
            "Restore staging operation directory");
        var stagedWorld = ResolveChild(stagingRoot, "world");
        EnsurePhysicalDirectory(
            stagedWorld,
            create: true,
            "Restore staged world directory");

        try
        {
            EnsureFreeSpace(instanceRoot, world.UncompressedBytes + FreeSpaceReserveBytes);
            backupRoot = EnsureBackupRootPhysical(instance.Id, create: false);
            ValidateBackupFilePhysical(backupRoot, backupPath);
            using var archive = ZipFile.OpenRead(backupPath);
            var prefix = world.ArchivePrefix.TrimEnd('/') + "/";
            var extractedFiles = 0;
            long extractedBytes = 0;
            var verifiedPaths = expectedFiles is null
                ? null
                : new HashSet<string>(StringComparer.Ordinal);
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
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                WorldBackupFile? expectedFile = null;
                if (expectedFiles is not null)
                {
                    if (!expectedFiles.TryGetValue(relative, out expectedFile))
                        throw new InvalidDataException(
                            $"Backup contains unexpected world file '{relative}'.");
                    if (!verifiedPaths!.Add(relative))
                        throw new InvalidDataException(
                            $"Backup contains duplicate world file '{relative}'.");
                    if (entry.Length != expectedFile.Size)
                        throw new InvalidDataException(
                            $"Backup file '{relative}' size does not match its manifest.");
                }

                if (extractedFiles >= world.FileCount
                    || entry.Length > world.UncompressedBytes - extractedBytes)
                    throw new InvalidDataException("The backup exceeds its declared world file totals.");

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var input = entry.Open();
                await using var output = new FileStream(
                    target,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var hash = expectedFile is null
                    ? null
                    : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[128 * 1024];
                long fileBytes = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
                    if (read == 0)
                        break;

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    hash?.AppendData(buffer, 0, read);
                    fileBytes = checked(fileBytes + read);
                }

                if (expectedFile is not null)
                {
                    if (fileBytes != expectedFile.Size)
                        throw new InvalidDataException(
                            $"Backup file '{relative}' extracted size does not match its manifest.");
                    var digest = Convert.ToHexString(hash!.GetHashAndReset()).ToLowerInvariant();
                    if (!string.Equals(digest, expectedFile.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException(
                            $"Backup file '{relative}' failed SHA-256 verification.");
                }

                extractedFiles++;
                extractedBytes = checked(extractedBytes + fileBytes);
            }

            // Validate before moving the current world into restore-safety.
            if (extractedFiles != world.FileCount || extractedBytes != world.UncompressedBytes)
                throw new InvalidDataException("The backup world files do not match the manifest totals.");
            if (expectedFiles is not null && verifiedPaths!.Count != expectedFiles.Count)
                throw new InvalidDataException("Backup is missing one or more world files from its manifest.");

            cancellationToken.ThrowIfCancellationRequested();

            string? safetyCopy = null;
            savesRoot = EnsureRestoreSavesRootPhysical(instance.Id);
            destinationWorld = ResolveChild(savesRoot, world.Name);
            if (Directory.Exists(destinationWorld))
            {
                RejectReparsePoint(destinationWorld);

                instanceRoot = _paths.EnsureInstanceDirectoryPhysical(instance.Id);
                var safetyParent = ResolveChild(instanceRoot, "restore-safety");
                EnsurePhysicalDirectory(
                    safetyParent,
                    create: true,
                    "Restore safety root");
                _paths.EnsureInstanceDirectoryPhysical(instance.Id);

                var safetyRoot = ResolveChild(
                    safetyParent,
                    $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}");
                EnsurePhysicalDirectory(
                    safetyRoot,
                    create: true,
                    "Restore safety operation directory");
                safetyCopy = ResolveChild(safetyRoot, world.Name);

                savesRoot = EnsureRestoreSavesRootPhysical(instance.Id);
                destinationWorld = ResolveChild(savesRoot, world.Name);
                RejectReparsePoint(destinationWorld);
                Directory.Move(destinationWorld, safetyCopy);
            }
            else if (File.Exists(destinationWorld))
            {
                throw new InvalidDataException(
                    "Restore destination is occupied by a file.");
            }

            try
            {
                savesRoot = EnsureRestoreSavesRootPhysical(instance.Id);
                destinationWorld = ResolveChild(savesRoot, world.Name);
                if (Directory.Exists(destinationWorld) || File.Exists(destinationWorld))
                    throw new IOException(
                        "Restore destination changed before publication.");

                _paths.EnsureInstanceDirectoryPhysical(instance.Id);
                Directory.Move(stagedWorld, destinationWorld);
            }
            catch
            {
                if (safetyCopy is not null && Directory.Exists(safetyCopy))
                {
                    try
                    {
                        savesRoot = EnsureRestoreSavesRootPhysical(instance.Id);
                        destinationWorld = ResolveChild(savesRoot, world.Name);
                        if (!Directory.Exists(destinationWorld)
                            && !File.Exists(destinationWorld))
                        {
                            Directory.Move(safetyCopy, destinationWorld);
                        }
                    }
                    catch
                    {
                        // Preserve the publication failure. The safety copy remains
                        // recoverable rather than being moved through an unsafe path.
                    }
                }
                throw;
            }

            return new WorldRestoreResult(world.Name, safetyCopy, DateTimeOffset.UtcNow);
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
        }
    }

    private async Task<WorldBackupInfo> ReadBackupAsync(
        string backupRoot,
        string path,
        CancellationToken cancellationToken)
    {
        ValidateBackupFilePhysical(backupRoot, path);
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("manifest.json")
            ?? throw new InvalidDataException("Backup manifest is missing.");
        var manifestBytes = await ReadManifestBytesAsync(entry, cancellationToken);

        BackupManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<BackupManifest>(manifestBytes, _json)
                ?? throw new InvalidDataException("Backup manifest is invalid.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Backup manifest contains malformed JSON.", ex);
        }

        ValidateBackupManifest(manifest);

        return new WorldBackupInfo(
            manifest.Id,
            manifest.InstanceId,
            manifest.InstanceName,
            manifest.Kind,
            manifest.CreatedAt,
            path,
            new FileInfo(path).Length,
            manifest.Worlds)
        {
            Schema = manifest.Schema
        };
    }

    private static async Task<byte[]> ReadManifestBytesAsync(
        ZipArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        if (entry.Length > MaxBackupManifestBytes)
            throw new InvalidDataException(
                $"Backup manifest exceeds the {MaxBackupManifestBytes}-byte limit.");

        await using var input = entry.Open();
        await using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        var total = 0;

        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
                break;

            total += read;
            if (total > MaxBackupManifestBytes)
                throw new InvalidDataException(
                    $"Backup manifest exceeds the {MaxBackupManifestBytes}-byte limit.");

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return output.ToArray();
    }

    private static void ValidateBackupManifest(BackupManifest manifest)
    {
        if (manifest.Schema is not (LegacyBackupSchema or BackupSchema))
            throw new InvalidDataException($"Unsupported backup schema {manifest.Schema}.");
        if (string.IsNullOrWhiteSpace(manifest.Id)
            || string.IsNullOrWhiteSpace(manifest.InstanceId)
            || string.IsNullOrWhiteSpace(manifest.InstanceName)
            || string.IsNullOrWhiteSpace(manifest.Kind)
            || manifest.Worlds is null)
            throw new InvalidDataException("Backup manifest is incomplete.");
        if (manifest.Worlds.Count == 0 || manifest.Worlds.Count > MaxBackupWorlds)
            throw new InvalidDataException("Backup manifest contains an invalid world count.");

        var worldNames = new HashSet<string>(StringComparer.Ordinal);
        var prefixes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var world in manifest.Worlds)
        {
            if (world is null)
                throw new InvalidDataException("Backup manifest contains a null world entry.");
            ValidateWorldName(world.Name);
            ValidateArchivePrefix(world.ArchivePrefix);
            if (!worldNames.Add(world.Name))
                throw new InvalidDataException($"Backup manifest contains duplicate world '{world.Name}'.");
            if (!prefixes.Add(world.ArchivePrefix))
                throw new InvalidDataException(
                    $"Backup manifest contains duplicate archive prefix '{world.ArchivePrefix}'.");
            if (world.FileCount < 0
                || world.UncompressedBytes < 0
                || world.UncompressedBytes > long.MaxValue - FreeSpaceReserveBytes)
                throw new InvalidDataException("Backup manifest contains invalid world file totals.");

            if (manifest.Schema == LegacyBackupSchema)
                continue;

            if (world.Files is null || world.Files.Count != world.FileCount)
                throw new InvalidDataException(
                    $"Backup world '{world.Name}' has incomplete file integrity metadata.");

            var paths = new HashSet<string>(StringComparer.Ordinal);
            long totalBytes = 0;
            foreach (var file in world.Files)
            {
                if (file is null)
                    throw new InvalidDataException(
                        $"Backup world '{world.Name}' contains a null file entry.");
                ValidateBackupFilePath(file.Path);
                if (!paths.Add(file.Path))
                    throw new InvalidDataException(
                        $"Backup world '{world.Name}' contains duplicate file path '{file.Path}'.");
                if (file.Size < 0)
                    throw new InvalidDataException(
                        $"Backup file '{file.Path}' has an invalid size.");
                if (!IsSha256(file.Sha256))
                    throw new InvalidDataException(
                        $"Backup file '{file.Path}' has an invalid SHA-256 digest.");
                try
                {
                    totalBytes = checked(totalBytes + file.Size);
                }
                catch (OverflowException ex)
                {
                    throw new InvalidDataException(
                        $"Backup world '{world.Name}' file sizes overflow the supported range.",
                        ex);
                }
            }

            if (totalBytes != world.UncompressedBytes)
                throw new InvalidDataException(
                    $"Backup world '{world.Name}' file sizes do not match its declared total.");
        }
    }

    private static void ValidateWorldName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaxBackupWorldNameLength
            || value is "." or ".."
            || Path.IsPathRooted(value)
            || value.Contains('/')
            || value.Contains('\\')
            || value.Any(char.IsControl)
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("Backup manifest contains an invalid world name.");
    }

    private static void ValidateArchivePrefix(string value)
    {
        if (!IsNormalizedArchiveRelativePath(value, MaxArchivePrefixLength))
            throw new InvalidDataException("Backup manifest contains an invalid archive prefix.");
    }

    private static void ValidateBackupFilePath(string value)
    {
        if (!IsNormalizedArchiveRelativePath(value, MaxBackupFilePathLength))
            throw new InvalidDataException("Backup manifest contains an invalid file path.");
    }

    private static bool IsNormalizedArchiveRelativePath(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maxLength
            || value.StartsWith("/", StringComparison.Ordinal)
            || value.EndsWith("/", StringComparison.Ordinal)
            || value.Contains('\\')
            || value.Any(char.IsControl))
            return false;

        var parts = value.Split('/');
        return parts.Length > 0
               && parts.All(part => part.Length > 0 && part is not "." and not "..");
    }

    private static bool IsSha256(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private async Task<(long Bytes, int Files, IReadOnlyList<WorldBackupFile> Integrity)>
        AddDirectoryToArchiveAsync(
            ZipArchive archive,
            string sourceRoot,
            string archivePrefix,
            CancellationToken cancellationToken)
    {
        long totalBytes = 0;
        var fileCount = 0;
        var integrity = new List<WorldBackupFile>();

        foreach (var file in EnumerateFilesSafe(sourceRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceRoot, file)
                .Replace(Path.DirectorySeparatorChar, '/');
            if (!IsNormalizedArchiveRelativePath(relative, MaxBackupFilePathLength))
                throw new InvalidDataException("A world file escaped its save directory.");

            var entry = archive.CreateEntry(
                $"{archivePrefix}/{relative}",
                CompressionLevel.Optimal);
            await using var input = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = entry.Open();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            long archivedBytes = 0;

            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0)
                    break;

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                hash.AppendData(buffer, 0, read);
                archivedBytes = checked(archivedBytes + read);
            }

            totalBytes = checked(totalBytes + archivedBytes);
            fileCount++;
            integrity.Add(new WorldBackupFile(
                relative,
                archivedBytes,
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()));
        }

        return (totalBytes, fileCount, integrity);
    }

    private async Task CopyCloneTreeAsync(
        string currentSource,
        string currentDestination,
        string sourceRoot,
        bool includeWorlds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RejectReparsePoint(currentSource);
        Directory.CreateDirectory(currentDestination);

        foreach (var directory in Directory.EnumerateDirectories(currentSource))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ShouldCopyClonePath(sourceRoot, directory, includeWorlds))
                continue;
            RejectReparsePoint(directory);
            var target = ResolveChild(currentDestination, Path.GetFileName(directory));
            await CopyCloneTreeAsync(directory, target, sourceRoot, includeWorlds, cancellationToken);
        }

        foreach (var file in Directory.EnumerateFiles(currentSource))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ShouldCopyClonePath(sourceRoot, file, includeWorlds))
                continue;
            RejectReparsePoint(file);
            var target = ResolveChild(currentDestination, Path.GetFileName(file));
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

    private long MeasureCloneBytes(string current, string sourceRoot, bool includeWorlds)
    {
        RejectReparsePoint(current);
        long total = 0;
        foreach (var directory in Directory.EnumerateDirectories(current))
        {
            if (!ShouldCopyClonePath(sourceRoot, directory, includeWorlds))
                continue;
            total = checked(total + MeasureCloneBytes(directory, sourceRoot, includeWorlds));
        }
        foreach (var file in Directory.EnumerateFiles(current))
        {
            if (!ShouldCopyClonePath(sourceRoot, file, includeWorlds))
                continue;
            RejectReparsePoint(file);
            total = checked(total + new FileInfo(file).Length);
        }
        return total;
    }

    private static long MeasureSafeDirectoryBytes(string root)
    {
        long total = 0;
        foreach (var file in EnumerateFilesSafe(root))
            total = checked(total + new FileInfo(file).Length);
        return total;
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root)
    {
        RejectReparsePoint(root);
        foreach (var file in Directory.EnumerateFiles(root))
        {
            RejectReparsePoint(file);
            yield return file;
        }
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            RejectReparsePoint(directory);
            foreach (var file in EnumerateFilesSafe(directory))
                yield return file;
        }
    }

    private async Task RewriteInstallStateAsync(
        string instanceRoot,
        GameInstance clone,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(instanceRoot, "install-state.json");
        if (!File.Exists(path))
            return;

        JsonObject node;
        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            var parsed = JsonNode.Parse(json);
            if (parsed is not JsonObject objectNode)
            {
                TryDeleteFile(path);
                return;
            }

            node = objectNode;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            TryDeleteFile(path);
            return;
        }

        if (node.ContainsKey("Id"))
            node["Id"] = clone.Id;
        if (node.ContainsKey("id"))
            node["id"] = clone.Id;
        if (!node.ContainsKey("Id") && !node.ContainsKey("id"))
            node["id"] = clone.Id;

        if (node.ContainsKey("Name"))
            node["Name"] = clone.Name;
        if (node.ContainsKey("name"))
            node["name"] = clone.Name;
        if (!node.ContainsKey("Name") && !node.ContainsKey("name"))
            node["name"] = clone.Name;

        await AtomicJsonFile.WriteAsync(
            path,
            node,
            _json,
            cancellationToken);
    }

    private string GetBackupRoot(string instanceId)
        => Path.Combine(_paths.GetDataRoot(), "backups", instanceId);

    private string EnsureBackupRootPhysical(
        string instanceId,
        bool create)
    {
        var dataRoot = _paths.EnsureDataRootPhysical();
        var backupsRoot = ResolveChild(dataRoot, "backups");
        EnsurePhysicalDirectory(
            backupsRoot,
            create,
            "Backup storage root");

        if (!Directory.Exists(backupsRoot))
            return ResolveChild(backupsRoot, instanceId);

        _paths.EnsureDataRootPhysical();
        EnsurePhysicalDirectory(
            backupsRoot,
            create: false,
            "Backup storage root");

        var instanceBackupRoot = ResolveChild(backupsRoot, instanceId);
        EnsurePhysicalDirectory(
            instanceBackupRoot,
            create,
            "Instance backup root");

        if (Directory.Exists(instanceBackupRoot))
        {
            EnsurePhysicalDirectory(
                backupsRoot,
                create: false,
                "Backup storage root");
            _paths.EnsureDataRootPhysical();
        }

        return instanceBackupRoot;
    }

    private string EnsureRestoreSavesRootPhysical(string instanceId)
    {
        var instanceRoot = _paths.EnsureInstanceDirectoryPhysical(instanceId);
        var gameRoot = ResolveChild(instanceRoot, "game");
        EnsurePhysicalDirectory(
            gameRoot,
            create: true,
            "Instance game directory");
        _paths.EnsureInstanceDirectoryPhysical(instanceId);

        var savesRoot = ResolveChild(gameRoot, "saves");
        EnsurePhysicalDirectory(
            savesRoot,
            create: true,
            "Instance saves directory");

        EnsurePhysicalDirectory(
            gameRoot,
            create: false,
            "Instance game directory");
        _paths.EnsureInstanceDirectoryPhysical(instanceId);
        return savesRoot;
    }

    private static void EnsurePhysicalDirectory(
        string path,
        bool create,
        string label)
    {
        if (Directory.Exists(path))
        {
            RejectReparsePoint(path);
            return;
        }

        if (File.Exists(path))
            throw new InvalidDataException(
                $"{label} is occupied by a file.");

        if (!create)
            return;

        Directory.CreateDirectory(path);
        if (!Directory.Exists(path))
            throw new IOException(
                $"{label} could not be created.");
        RejectReparsePoint(path);
    }

    private static string ValidateBackupFilePhysical(
        string backupRoot,
        string path,
        bool requireZipExtension = true)
    {
        var root = Path.GetFullPath(backupRoot);
        var candidate = Path.GetFullPath(path);
        EnsureContained(root, candidate);

        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException(
                $"Backup root is missing: {root}");
        RejectReparsePoint(root);

        var parent = Path.GetDirectoryName(candidate)
            ?? throw new InvalidDataException(
                "Backup file has no parent directory.");
        if (!PathEquals(parent, root))
            throw new InvalidDataException(
                "Backup files must be direct children of the instance backup root.");

        if (requireZipExtension
            && !candidate.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Backup file must use the .zip extension.");

        if (!File.Exists(candidate))
            throw new FileNotFoundException(
                "The selected backup file no longer exists.",
                candidate);

        var attributes = File.GetAttributes(candidate);
        if ((attributes & FileAttributes.Directory) != 0
            || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "Backup files must be physical regular files; symbolic links and reparse points are not allowed.");
        }

        return candidate;
    }

    private static bool PathEquals(string left, string right)
        => string.Equals(
            Path.GetFullPath(left).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

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

    private static void EnsureFreeSpace(string path, long requiredBytes)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrWhiteSpace(root))
            return;

        DriveInfo drive;
        try
        {
            drive = new DriveInfo(root);
            if (!drive.IsReady)
                return;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Some virtual filesystems do not expose DriveInfo. Atomic staging still protects originals.
            return;
        }

        if (drive.AvailableFreeSpace < requiredBytes)
            throw new IOException(
                $"Not enough free space. Need about {requiredBytes / (1024 * 1024)} MiB, " +
                $"available {drive.AvailableFreeSpace / (1024 * 1024)} MiB.");
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
            if (!Directory.Exists(path))
                return;

            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(path);
            else
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
