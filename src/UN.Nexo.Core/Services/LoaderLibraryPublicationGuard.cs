namespace UN.Nexo.Core.Services;

internal static class LoaderLibraryPublicationGuard
{
    internal static string EnsureDestinationParentPhysical(
        NexoPathService paths,
        string instanceId,
        string destinationPath,
        bool create,
        string label)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        var destinationParent =
            Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidDataException(
                $"{label} destination has no parent directory.");

        var instanceRoot =
            paths.EnsureInstanceDirectoryPhysical(
                instanceId);

        EnsurePhysicalDirectoryChain(
            instanceRoot,
            destinationParent,
            create,
            label + " directory");

        paths.EnsureInstanceDirectoryPhysical(
            instanceId);

        return destinationParent;
    }

    internal static void RejectReparsePointIfPresent(
        string path,
        string label)
    {
        if (!Directory.Exists(path)
            && !File.Exists(path))
        {
            return;
        }

        if ((File.GetAttributes(path)
             & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"{label} must not be a symbolic link, junction or other reparse point.");
        }
    }

    internal static void TryDeleteTempFile(
        NexoPathService paths,
        string instanceId,
        string tempPath,
        string label)
    {
        try
        {
            var parent =
                Path.GetDirectoryName(tempPath);
            if (parent is null)
                return;

            var instanceRoot =
                paths.EnsureInstanceDirectoryPhysical(
                    instanceId);

            EnsurePhysicalDirectoryChain(
                instanceRoot,
                parent,
                create: false,
                label + " directory");

            if (!File.Exists(tempPath))
                return;

            var attributes =
                File.GetAttributes(tempPath);
            if ((attributes & FileAttributes.Directory) != 0
                || (attributes & FileAttributes.ReparsePoint) != 0)
            {
                return;
            }

            File.Delete(tempPath);
        }
        catch
        {
            // Fail closed if the parent chain changed. Do not follow an
            // untrusted replacement during cleanup.
        }
    }

    private static void EnsurePhysicalDirectoryChain(
        string root,
        string target,
        bool create,
        string label)
    {
        var rootFull =
            Path.GetFullPath(root)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
        var targetFull =
            Path.GetFullPath(target)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
        var comparison =
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
        var rootWithSeparator =
            rootFull + Path.DirectorySeparatorChar;

        if (!string.Equals(
                rootFull,
                targetFull,
                comparison)
            && !targetFull.StartsWith(
                rootWithSeparator,
                comparison))
        {
            throw new InvalidDataException(
                $"{label} escaped the managed instance directory.");
        }

        RejectDirectoryReparsePoint(
            rootFull,
            label);

        if (string.Equals(
                rootFull,
                targetFull,
                comparison))
        {
            return;
        }

        var relative =
            Path.GetRelativePath(
                rootFull,
                targetFull);
        var current =
            rootFull;

        foreach (var component in relative.Split(
                     [
                         Path.DirectorySeparatorChar,
                         Path.AltDirectorySeparatorChar
                     ],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current =
                Path.Combine(
                    current,
                    component);

            if (Directory.Exists(current))
            {
                RejectDirectoryReparsePoint(
                    current,
                    label);
                continue;
            }

            if (File.Exists(current))
            {
                throw new InvalidDataException(
                    $"{label} is occupied by a file: {current}");
            }

            if (!create)
            {
                throw new DirectoryNotFoundException(
                    $"{label} disappeared before publication: {current}");
            }

            var parent =
                Path.GetDirectoryName(current)
                ?? throw new InvalidDataException(
                    $"{label} has no parent directory.");

            RejectDirectoryReparsePoint(
                parent,
                label);

            Directory.CreateDirectory(
                current);

            RejectDirectoryReparsePoint(
                current,
                label);
            RejectDirectoryReparsePoint(
                parent,
                label);
        }
    }

    private static void RejectDirectoryReparsePoint(
        string path,
        string label)
    {
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException(
                $"{label} is missing: {path}");
        }

        var attributes =
            File.GetAttributes(path);

        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw new InvalidDataException(
                $"{label} must be a directory: {path}");
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"{label} must not contain symbolic links, junctions or other reparse points: {path}");
        }
    }
}
