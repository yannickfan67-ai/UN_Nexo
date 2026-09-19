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
        if (modsDirectory is null) return [];
        return Directory.EnumerateFiles(modsDirectory, "*", SearchOption.TopDirectoryOnly).Where(path => IsManagedModFileName(Path.GetFileName(path))).Select(CreateModel).OrderBy(mod => mod.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<InstalledMod> InstallAsync(string instanceId, string sourcePath, bool replaceExisting = true, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("A local mod JAR path is required.", nameof(sourcePath));
        var fullSourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullSourcePath)) throw new FileNotFoundException("The selected mod JAR does not exist.", fullSourcePath);
        var fileName = Path.GetFileName(fullSourcePath);
        if (!fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Only .jar files can be installed as mods.");
        ValidateManagedFileName(fileName, false);

        await using var operationLease = await _operations.AcquireAsync(
            instanceId,
            "mod install",
            cancellationToken);
        using var lease = await PathKeyedLock.AcquireAsync(
            GetMutationKey(instanceId, fileName),
            cancellationToken);
        var modsDirectory = VerifyModsDirectory(instanceId, create: true)!;
        var destinationPath = ResolveManagedPath(modsDirectory, fileName, false);
        var disabledPath = ResolveManagedPath(modsDirectory, fileName + DisabledSuffix, true);
        var temporaryPath = destinationPath + ".tmp-" + Guid.NewGuid().ToString("N");
        if (!replaceExisting && (File.Exists(destinationPath) || File.Exists(disabledPath))) throw new IOException($"A mod named '{fileName}' is already installed in this instance.");
        try
        {
            await using (var source = new FileStream(fullSourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }
            VerifyPhysicalDirectory(modsDirectory);
            File.Move(temporaryPath, destinationPath, replaceExisting);
            if (File.Exists(disabledPath)) File.Delete(disabledPath);
            return CreateModel(destinationPath);
        }
        finally { try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { } }
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
