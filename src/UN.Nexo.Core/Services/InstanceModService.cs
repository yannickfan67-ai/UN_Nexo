using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed class InstanceModService
{
    private const string DisabledSuffix = ".disabled";
    private readonly NexoPathService _paths;

    public InstanceModService(NexoPathService paths)
    {
        _paths = paths;
    }

    public string GetModsDirectory(string instanceId)
        => Path.Combine(_paths.GetInstanceGameDirectory(instanceId), "mods");

    public IReadOnlyList<InstalledMod> List(string instanceId)
    {
        var modsDirectory = GetModsDirectory(instanceId);
        if (!Directory.Exists(modsDirectory))
            return [];

        return Directory.EnumerateFiles(modsDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => IsManagedModFileName(Path.GetFileName(path)))
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
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("A local mod JAR path is required.", nameof(sourcePath));

        var fullSourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullSourcePath))
            throw new FileNotFoundException("The selected mod JAR does not exist.", fullSourcePath);

        var fileName = Path.GetFileName(fullSourcePath);
        if (!fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only .jar files can be installed as mods.");

        ValidateManagedFileName(fileName, allowDisabled: false);

        var modsDirectory = GetModsDirectory(instanceId);
        Directory.CreateDirectory(modsDirectory);
        var destinationPath = ResolveManagedPath(modsDirectory, fileName, allowDisabled: false);
        var temporaryPath = destinationPath + ".tmp-" + Guid.NewGuid().ToString("N");

        try
        {
            await using (var source = new FileStream(
                fullSourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, destinationPath, replaceExisting);
            return CreateModel(destinationPath);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // Best-effort cleanup only. The completed destination, if any, is authoritative.
            }
        }
    }

    public InstalledMod SetEnabled(string instanceId, string fileName, bool enabled)
    {
        ValidateManagedFileName(fileName, allowDisabled: true);
        var modsDirectory = GetModsDirectory(instanceId);
        var currentPath = ResolveManagedPath(modsDirectory, fileName, allowDisabled: true);
        if (!File.Exists(currentPath))
            throw new FileNotFoundException("The selected mod no longer exists.", currentPath);

        var currentlyEnabled = fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
        if (currentlyEnabled == enabled)
            return CreateModel(currentPath);

        var targetFileName = enabled
            ? fileName[..^DisabledSuffix.Length]
            : fileName + DisabledSuffix;
        var targetPath = ResolveManagedPath(modsDirectory, targetFileName, allowDisabled: true);
        if (File.Exists(targetPath))
            throw new IOException($"Cannot change mod state because '{targetFileName}' already exists.");

        File.Move(currentPath, targetPath);
        return CreateModel(targetPath);
    }

    public void Remove(string instanceId, string fileName)
    {
        ValidateManagedFileName(fileName, allowDisabled: true);
        var modsDirectory = GetModsDirectory(instanceId);
        var path = ResolveManagedPath(modsDirectory, fileName, allowDisabled: true);
        if (!File.Exists(path))
            throw new FileNotFoundException("The selected mod no longer exists.", path);

        File.Delete(path);
    }

    private static InstalledMod CreateModel(string path)
    {
        var info = new FileInfo(path);
        var fileName = info.Name;
        var enabled = fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
        return new InstalledMod(fileName, enabled, info.Length, info.LastWriteTimeUtc);
    }

    private static bool IsManagedModFileName(string fileName)
        => fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
           || fileName.EndsWith(".jar" + DisabledSuffix, StringComparison.OrdinalIgnoreCase);

    private static void ValidateManagedFileName(string fileName, bool allowDisabled)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName is "." or ".."
            || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("Invalid mod file name.");

        var valid = fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
            || allowDisabled && fileName.EndsWith(".jar" + DisabledSuffix, StringComparison.OrdinalIgnoreCase);
        if (!valid)
            throw new InvalidDataException("Only .jar mods (or .jar.disabled entries) are managed here.");
    }

    private static string ResolveManagedPath(string modsDirectory, string fileName, bool allowDisabled)
    {
        ValidateManagedFileName(fileName, allowDisabled);
        var fullDirectory = Path.GetFullPath(modsDirectory);
        var candidate = Path.GetFullPath(Path.Combine(fullDirectory, fileName));
        var rootWithSeparator = Path.EndsInDirectorySeparator(fullDirectory)
            ? fullDirectory
            : fullDirectory + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!candidate.StartsWith(rootWithSeparator, comparison))
            throw new InvalidDataException("Mod path must remain inside the instance mods directory.");
        return candidate;
    }
}
