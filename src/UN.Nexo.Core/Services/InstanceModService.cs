using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed class InstanceModService
{
    private const string DisabledSuffix = ".disabled";
    private readonly NexoPathService _paths;
    private readonly InstanceOperationCoordinator _operations;

    public InstanceModService(
        NexoPathService paths,
        InstanceOperationCoordinator? operations = null)
    {
        _paths = paths;
        _operations = operations ?? new InstanceOperationCoordinator(paths);
    }
    public string GetModsDirectory(string instanceId) => Path.Combine(_paths.GetInstanceGameDirectory(instanceId), "mods");

    public IReadOnlyList<InstalledMod> List(string instanceId)
    {
        var modsDirectory = VerifyModsDirectory(instanceId, create: false);
        if (modsDirectory is null)
            return [];

        return Directory
            .EnumerateFiles(modsDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(path =>
                IsManagedModFileName(Path.GetFileName(path))
                && IsPhysicalManagedModPath(path))
            .Select(CreateModel)
            .OrderBy(mod => mod.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<InstalledMod> InstallAsync(
        string instanceId,
        string sourcePath,
        bool replaceExisting = true,
        CancellationToken cancellationToken = default)
    {
        var (fullSourcePath, fileName) = ValidateInstallSource(sourcePath);

        await using var operationLease = await _operations.AcquireAsync(
            instanceId,
            "mod install",
            cancellationToken);
        return await InstallCoreAsync(
            instanceId,
            fullSourcePath,
            fileName,
            replaceExisting,
            cancellationToken);
    }

    internal async Task<InstalledMod> InstallProviderUpdateAsync(
        string instanceId,
        string sourcePath,
        string? existingFileName,
        bool existingIsEnabled,
        CancellationToken cancellationToken = default)
    {
        var (fullSourcePath, fileName) = ValidateInstallSource(sourcePath);
        EnsurePhysicalRegularFile(
            fullSourcePath,
            "The staged provider mod must be a physical regular file.");

        await using var operationLease = await _operations.AcquireAsync(
            instanceId,
            existingFileName is null ? "Modrinth install" : "Modrinth update",
            cancellationToken);

        if (existingFileName is null)
        {
            return await InstallCoreAsync(
                instanceId,
                fullSourcePath,
                fileName,
                replaceExisting: true,
                cancellationToken);
        }

        ValidateManagedFileName(existingFileName, true);
        var modsDirectory = VerifyModsDirectory(instanceId, create: true)!;
        if (!TryResolvePhysicalManagedModPath(
                modsDirectory,
                existingFileName,
                out var existingPath))
        {
            throw new FileNotFoundException(
                "The previously matched mod no longer exists as a physical managed file.",
                ResolveManagedPath(modsDirectory, existingFileName, true));
        }

        var finalFileName = existingIsEnabled
            ? fileName
            : fileName + DisabledSuffix;
        var finalPath = ResolveManagedPath(
            modsDirectory,
            finalFileName,
            allowDisabled: true);

        if (FileNameEquals(existingFileName, finalFileName))
        {
            using var sameFileLease = await PathKeyedLock.AcquireAsync(
                GetMutationKey(instanceId, finalFileName),
                cancellationToken);
            if (!TryResolvePhysicalManagedModPath(
                    modsDirectory,
                    existingFileName,
                    out existingPath))
            {
                throw new FileNotFoundException(
                    "The previously matched mod changed before the update could be published.",
                    existingPath);
            }

            return await ReplaceExactManagedFileAsync(
                modsDirectory,
                fullSourcePath,
                finalPath,
                cancellationToken);
        }

        if (File.Exists(finalPath))
        {
            throw new IOException(
                $"Cannot update this Modrinth project because '{finalFileName}' already exists.");
        }

        using (var newFileLease = await PathKeyedLock.AcquireAsync(
                   GetMutationKey(instanceId, finalFileName),
                   cancellationToken))
        {
            if (File.Exists(finalPath))
            {
                throw new IOException(
                    $"Cannot update this Modrinth project because '{finalFileName}' already exists.");
            }

            await PublishNewManagedFileAsync(
                modsDirectory,
                fullSourcePath,
                finalPath,
                cancellationToken);
        }

        try
        {
            using var oldFileLease = await PathKeyedLock.AcquireAsync(
                GetMutationKey(instanceId, existingFileName),
                cancellationToken);
            if (!TryResolvePhysicalManagedModPath(
                    modsDirectory,
                    existingFileName,
                    out existingPath))
            {
                throw new IOException(
                    "The previous Modrinth file changed while the update was in progress.");
            }

            VerifyPhysicalDirectory(modsDirectory);
            File.Delete(existingPath);
        }
        catch
        {
            // The instance operation lease is still held here, so Play/backup/repair
            // cannot observe the transitional two-file state. Best-effort rollback
            // removes the newly published file and leaves the old file recoverable.
            using var rollbackLease = PathKeyedLock.Acquire(
                GetMutationKey(instanceId, finalFileName));
            if (TryResolvePhysicalManagedModPath(
                    modsDirectory,
                    finalFileName,
                    out var rollbackPath))
            {
                try
                {
                    File.Delete(rollbackPath);
                }
                catch
                {
                }
            }

            throw;
        }

        return CreateModel(finalPath);
    }

    internal async Task<IReadOnlyList<InstalledMod>> InstallProviderBatchAsync(
        string instanceId,
        IReadOnlyList<ProviderModBatchItem> items,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            throw new ArgumentException("An instance id is required.", nameof(instanceId));
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
            return [];

        var validated = new List<(
            string SourcePath,
            string FileName,
            string? ExistingFileName,
            bool ExistingIsEnabled)>(items.Count);

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (sourcePath, fileName) = ValidateInstallSource(item.SourcePath);
            EnsurePhysicalRegularFile(
                sourcePath,
                "The staged provider mod must be a physical regular file.");
            if (item.ExistingFileName is not null)
                ValidateManagedFileName(item.ExistingFileName, allowDisabled: true);
            validated.Add((
                sourcePath,
                fileName,
                item.ExistingFileName,
                item.ExistingIsEnabled));
        }

        await using var operationLease = await _operations.AcquireAsync(
            instanceId,
            "provider dependency batch install",
            cancellationToken);

        var modsDirectory = VerifyModsDirectory(instanceId, create: true)!;
        var mutations = new List<BatchMutation>(validated.Count);
        var finalNames = new HashSet<string>(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        var existingNames = new HashSet<string>(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);

        foreach (var item in validated)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var finalFileName = item.ExistingFileName is not null && !item.ExistingIsEnabled
                ? item.FileName + DisabledSuffix
                : item.FileName;
            if (!finalNames.Add(finalFileName))
                throw new IOException(
                    $"The provider dependency plan contains duplicate destination '{finalFileName}'.");

            string? existingPath = null;
            if (item.ExistingFileName is not null)
            {
                if (!existingNames.Add(item.ExistingFileName))
                    throw new IOException(
                        $"The provider dependency plan updates '{item.ExistingFileName}' more than once.");
                if (!TryResolvePhysicalManagedModPath(
                        modsDirectory,
                        item.ExistingFileName,
                        out existingPath))
                {
                    throw new FileNotFoundException(
                        "A previously matched provider mod no longer exists as a physical managed file.",
                        ResolveManagedPath(
                            modsDirectory,
                            item.ExistingFileName,
                            allowDisabled: true));
                }
            }

            var finalPath = ResolveManagedPath(
                modsDirectory,
                finalFileName,
                allowDisabled: true);
            if (File.Exists(finalPath)
                && (existingPath is null || !PathEquals(existingPath, finalPath)))
            {
                throw new IOException(
                    $"Cannot publish provider dependency '{finalFileName}' because that file already exists.");
            }

            if (existingPath is null)
            {
                var counterpartName = finalFileName.EndsWith(
                    ".jar" + DisabledSuffix,
                    StringComparison.OrdinalIgnoreCase)
                    ? finalFileName[..^DisabledSuffix.Length]
                    : finalFileName + DisabledSuffix;
                var counterpartPath = ResolveManagedPath(
                    modsDirectory,
                    counterpartName,
                    allowDisabled: true);
                if (File.Exists(counterpartPath))
                {
                    throw new IOException(
                        $"Cannot publish provider dependency '{finalFileName}' because '{counterpartName}' already exists.");
                }
            }

            mutations.Add(new BatchMutation(
                item.SourcePath,
                existingPath,
                finalPath,
                TemporaryPath(finalPath, "batch-new"),
                existingPath is null
                    ? null
                    : TemporaryPath(existingPath, "batch-old")));
        }

        var oldPaths = mutations
            .Where(item => item.ExistingPath is not null)
            .Select(item => item.ExistingPath!)
            .ToArray();
        foreach (var mutation in mutations)
        {
            if (oldPaths.Any(oldPath =>
                    !PathEquals(oldPath, mutation.ExistingPath ?? string.Empty)
                    && PathEquals(oldPath, mutation.FinalPath)))
            {
                throw new IOException(
                    "The provider dependency plan would overwrite another mod that is also being updated.");
            }
        }

        try
        {
            foreach (var mutation in mutations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await CopyPhysicalFileAsync(
                    mutation.SourcePath,
                    mutation.PreparedPath,
                    requirePhysicalSource: true,
                    cancellationToken);
            }

            foreach (var mutation in mutations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (mutation.ExistingPath is null)
                    continue;

                if (!IsPhysicalManagedModPath(mutation.ExistingPath))
                    throw new IOException(
                        "A provider mod changed before the dependency batch could be published.");
                VerifyPhysicalDirectory(modsDirectory);
                File.Move(
                    mutation.ExistingPath,
                    mutation.BackupPath!,
                    overwrite: false);
                mutation.OldMoved = true;
            }

            foreach (var mutation in mutations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                VerifyPhysicalDirectory(modsDirectory);
                EnsurePhysicalRegularFile(
                    mutation.PreparedPath,
                    "A staged dependency changed before publication.");
                File.Move(
                    mutation.PreparedPath,
                    mutation.FinalPath,
                    overwrite: false);
                mutation.NewPublished = true;
            }

            foreach (var mutation in mutations)
            {
                if (mutation.BackupPath is not null)
                    TryDelete(mutation.BackupPath);
            }

            return mutations
                .Select(mutation => CreateModel(mutation.FinalPath))
                .ToArray();
        }
        catch
        {
            for (var index = mutations.Count - 1; index >= 0; index--)
            {
                var mutation = mutations[index];
                if (mutation.NewPublished)
                    TryDelete(mutation.FinalPath);

                if (mutation.OldMoved
                    && mutation.ExistingPath is not null
                    && mutation.BackupPath is not null
                    && File.Exists(mutation.BackupPath)
                    && !File.Exists(mutation.ExistingPath))
                {
                    try
                    {
                        VerifyPhysicalDirectory(modsDirectory);
                        File.Move(
                            mutation.BackupPath,
                            mutation.ExistingPath,
                            overwrite: false);
                    }
                    catch
                    {
                    }
                }
            }

            throw;
        }
        finally
        {
            foreach (var mutation in mutations)
            {
                TryDelete(mutation.PreparedPath);
                if (mutation.BackupPath is not null)
                    TryDelete(mutation.BackupPath);
            }
        }
    }

    public InstalledMod SetEnabled(string instanceId, string fileName, bool enabled)
        => SetEnabledAsync(instanceId, fileName, enabled).GetAwaiter().GetResult();

    public async Task<InstalledMod> SetEnabledAsync(
        string instanceId,
        string fileName,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ValidateManagedFileName(fileName, true);

        // Lock ordering is always instance lease first, then the per-mod path lock.
        await using var operationLease = await _operations.AcquireAsync(
            instanceId,
            enabled ? "mod enable" : "mod disable",
            cancellationToken);
        using var lease = await PathKeyedLock.AcquireAsync(
            GetMutationKey(instanceId, fileName),
            cancellationToken);

        var modsDirectory = VerifyModsDirectory(instanceId, create: false)
            ?? throw new DirectoryNotFoundException("The instance mods directory does not exist.");
        var currentPath = ResolveManagedPath(modsDirectory, fileName, true);
        if (!File.Exists(currentPath))
            throw new FileNotFoundException("The selected mod no longer exists.", currentPath);

        var currentlyEnabled = fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
        if (currentlyEnabled == enabled)
            return CreateModel(currentPath);

        var targetFileName = enabled
            ? fileName[..^DisabledSuffix.Length]
            : fileName + DisabledSuffix;
        var targetPath = ResolveManagedPath(modsDirectory, targetFileName, true);
        if (File.Exists(targetPath))
            throw new IOException($"Cannot change mod state because '{targetFileName}' already exists.");

        VerifyPhysicalDirectory(modsDirectory);
        File.Move(currentPath, targetPath);
        return CreateModel(targetPath);
    }

    public void Remove(string instanceId, string fileName)
        => RemoveAsync(instanceId, fileName).GetAwaiter().GetResult();

    public async Task RemoveAsync(
        string instanceId,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        ValidateManagedFileName(fileName, true);

        await using var operationLease = await _operations.AcquireAsync(
            instanceId,
            "mod remove",
            cancellationToken);
        using var lease = await PathKeyedLock.AcquireAsync(
            GetMutationKey(instanceId, fileName),
            cancellationToken);

        var modsDirectory = VerifyModsDirectory(instanceId, create: false)
            ?? throw new DirectoryNotFoundException("The instance mods directory does not exist.");
        var path = ResolveManagedPath(modsDirectory, fileName, true);
        if (!File.Exists(path))
            throw new FileNotFoundException("The selected mod no longer exists.", path);

        VerifyPhysicalDirectory(modsDirectory);
        File.Delete(path);
    }

    internal static bool TryResolvePhysicalManagedModPath(
        string modsDirectory,
        string fileName,
        out string path)
    {
        path = string.Empty;
        try
        {
            if (!Directory.Exists(modsDirectory))
                return false;

            VerifyPhysicalDirectory(modsDirectory);
            var candidate = ResolveManagedPath(
                modsDirectory,
                fileName,
                allowDisabled: true);
            if (!File.Exists(candidate) || !IsPhysicalManagedModPath(candidate))
                return false;

            path = candidate;
            return true;
        }
        catch (Exception ex) when (
            ex is ArgumentException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            return false;
        }
    }

    private async Task<InstalledMod> InstallCoreAsync(
        string instanceId,
        string fullSourcePath,
        string fileName,
        bool replaceExisting,
        CancellationToken cancellationToken)
    {
        using var lease = await PathKeyedLock.AcquireAsync(
            GetMutationKey(instanceId, fileName),
            cancellationToken);
        var modsDirectory = VerifyModsDirectory(instanceId, create: true)!;
        var destinationPath = ResolveManagedPath(
            modsDirectory,
            fileName,
            allowDisabled: false);
        var disabledPath = ResolveManagedPath(
            modsDirectory,
            fileName + DisabledSuffix,
            allowDisabled: true);
        var temporaryPath =
            destinationPath + ".tmp-" + Guid.NewGuid().ToString("N");

        if (!replaceExisting
            && (File.Exists(destinationPath) || File.Exists(disabledPath)))
        {
            throw new IOException(
                $"A mod named '{fileName}' is already installed in this instance.");
        }

        try
        {
            await CopyPhysicalFileAsync(
                fullSourcePath,
                temporaryPath,
                requirePhysicalSource: false,
                cancellationToken);
            VerifyPhysicalDirectory(modsDirectory);
            File.Move(temporaryPath, destinationPath, replaceExisting);
            if (File.Exists(disabledPath))
                File.Delete(disabledPath);
            return CreateModel(destinationPath);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static async Task<InstalledMod> ReplaceExactManagedFileAsync(
        string modsDirectory,
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var temporaryPath =
            destinationPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await CopyPhysicalFileAsync(
                sourcePath,
                temporaryPath,
                requirePhysicalSource: true,
                cancellationToken);
            VerifyPhysicalDirectory(modsDirectory);
            EnsurePhysicalRegularFile(
                sourcePath,
                "The staged provider mod changed before publication.");
            File.Move(temporaryPath, destinationPath, overwrite: true);
            return CreateModel(destinationPath);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static async Task PublishNewManagedFileAsync(
        string modsDirectory,
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var temporaryPath =
            destinationPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await CopyPhysicalFileAsync(
                sourcePath,
                temporaryPath,
                requirePhysicalSource: true,
                cancellationToken);
            VerifyPhysicalDirectory(modsDirectory);
            EnsurePhysicalRegularFile(
                sourcePath,
                "The staged provider mod changed before publication.");
            File.Move(temporaryPath, destinationPath, overwrite: false);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static async Task CopyPhysicalFileAsync(
        string sourcePath,
        string destinationPath,
        bool requirePhysicalSource,
        CancellationToken cancellationToken)
    {
        if (requirePhysicalSource)
        {
            EnsurePhysicalRegularFile(
                sourcePath,
                "The staged provider mod must be a physical regular file.");
        }

        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (requirePhysicalSource)
        {
            EnsurePhysicalRegularFile(
                sourcePath,
                "The staged provider mod changed before it could be read.");
        }

        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, cancellationToken);
        await destination.FlushAsync(cancellationToken);
    }

    private static (string FullSourcePath, string FileName) ValidateInstallSource(
        string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ArgumentException(
                "A local mod JAR path is required.",
                nameof(sourcePath));
        }

        var fullSourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullSourcePath))
        {
            throw new FileNotFoundException(
                "The selected mod JAR does not exist.",
                fullSourcePath);
        }

        var fileName = Path.GetFileName(fullSourcePath);
        if (!fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only .jar files can be installed as mods.");

        ValidateManagedFileName(fileName, allowDisabled: false);
        return (fullSourcePath, fileName);
    }

    private static bool IsPhysicalManagedModPath(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.Directory) == 0
                   && (attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception ex) when (
            ex is IOException
            or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void EnsurePhysicalRegularFile(
        string path,
        string message)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) != 0
            || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(message);
        }
    }

    private static string TemporaryPath(string path, string kind)
        => path + "." + kind + "-" + Guid.NewGuid().ToString("N") + ".tmp";

    private static bool PathEquals(string left, string right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
            return false;
        return string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    private sealed class BatchMutation(
        string sourcePath,
        string? existingPath,
        string finalPath,
        string preparedPath,
        string? backupPath)
    {
        public string SourcePath { get; } = sourcePath;
        public string? ExistingPath { get; } = existingPath;
        public string FinalPath { get; } = finalPath;
        public string PreparedPath { get; } = preparedPath;
        public string? BackupPath { get; } = backupPath;
        public bool OldMoved { get; set; }
        public bool NewPublished { get; set; }
    }

    private static bool FileNameEquals(string left, string right)
        => string.Equals(
            left,
            right,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static void TryDelete(string path)
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

    private string GetMutationKey(string instanceId, string fileName)
    {
        var baseFileName = fileName.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase) ? fileName[..^DisabledSuffix.Length] : fileName;
        ValidateManagedFileName(baseFileName, false);
        return Path.GetFullPath(Path.Combine(GetModsDirectory(instanceId), baseFileName));
    }

    private string? VerifyModsDirectory(string instanceId, bool create)
    {
        var instanceDirectory = _paths.GetInstanceDirectory(instanceId);
        var gameDirectory = _paths.GetInstanceGameDirectory(instanceId);
        var modsDirectory = GetModsDirectory(instanceId);
        RejectLinkIfPresent(instanceDirectory); RejectLinkIfPresent(gameDirectory); RejectLinkIfPresent(modsDirectory);
        if (!Directory.Exists(modsDirectory)) { if (!create) return null; Directory.CreateDirectory(modsDirectory); }
        VerifyPhysicalDirectory(modsDirectory); return modsDirectory;
    }

    private static void RejectLinkIfPresent(string path) { if (Directory.Exists(path) || File.Exists(path)) VerifyPhysicalDirectory(path); }
    private static void VerifyPhysicalDirectory(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0) throw new InvalidDataException("The managed mods path must be a directory.");
        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked or reparse-point directories cannot be used for managed mods.");
    }
    private static InstalledMod CreateModel(string path) { var info = new FileInfo(path); var fileName = info.Name; return new InstalledMod(fileName, fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase), info.Length, info.LastWriteTimeUtc); }
    private static bool IsManagedModFileName(string fileName) => fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".jar" + DisabledSuffix, StringComparison.OrdinalIgnoreCase);
    private static void ValidateManagedFileName(string fileName, bool allowDisabled)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or ".." || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new InvalidDataException("Invalid mod file name.");
        var valid = fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || allowDisabled && fileName.EndsWith(".jar" + DisabledSuffix, StringComparison.OrdinalIgnoreCase);
        if (!valid) throw new InvalidDataException("Only .jar mods (or .jar.disabled entries) are managed here.");
    }
    private static string ResolveManagedPath(string modsDirectory, string fileName, bool allowDisabled)
    {
        ValidateManagedFileName(fileName, allowDisabled);
        var fullDirectory = Path.GetFullPath(modsDirectory); var candidate = Path.GetFullPath(Path.Combine(fullDirectory, fileName));
        var rootWithSeparator = Path.EndsInDirectorySeparator(fullDirectory) ? fullDirectory : fullDirectory + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!candidate.StartsWith(rootWithSeparator, comparison)) throw new InvalidDataException("Mod path must remain inside the instance mods directory.");
        return candidate;
    }
}
