namespace UN.Nexo.Core.Services;

internal static class PhysicalPathGuard
{
    internal static void EnsureDirectoryForFile(
        string filePath,
        string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException(
                $"{label} has no parent directory.");

        EnsureExistingDirectoryChainPhysical(
            directory,
            label + " parent");
        Directory.CreateDirectory(directory);
        EnsureDirectoryChainPhysical(
            directory,
            label + " parent");
    }

    internal static void EnsureDirectoryChainPhysical(
        string directory,
        string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var current = Path.GetFullPath(directory);
        while (true)
        {
            if (!Directory.Exists(current))
            {
                if (File.Exists(current))
                    throw new InvalidDataException(
                        $"{label} contains a file where a directory is required.");

                throw new DirectoryNotFoundException(
                    $"{label} directory is missing: {current}");
            }

            EnsurePhysicalDirectory(current, label);

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent)
                || string.Equals(
                    parent,
                    current,
                    PathComparison))
                break;

            current = parent;
        }
    }

    internal static void EnsureRegularFileOrMissing(
        string path,
        string label)
    {
        var fullPath = Path.GetFullPath(path);

        if (!File.Exists(fullPath)
            && !Directory.Exists(fullPath))
            return;

        var attributes = File.GetAttributes(fullPath);
        if ((attributes & FileAttributes.Directory) != 0)
            throw new InvalidDataException(
                $"{label} must not be a directory.");

        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException(
                $"{label} cannot be a symbolic link or reparse point.");
    }

    internal static bool CanSafelyDeleteRegularFile(
        string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var parent = Path.GetDirectoryName(fullPath);
            if (parent is null)
                return false;

            EnsureDirectoryChainPhysical(
                parent,
                "cleanup parent");

            if (!File.Exists(fullPath))
                return false;

            var attributes = File.GetAttributes(fullPath);
            return (attributes & (
                    FileAttributes.Directory
                    | FileAttributes.ReparsePoint)) == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureExistingDirectoryChainPhysical(
        string directory,
        string label)
    {
        var current = Path.GetFullPath(directory);
        while (!Directory.Exists(current))
        {
            if (File.Exists(current))
                throw new InvalidDataException(
                    $"{label} contains a file where a directory is required.");

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent)
                || string.Equals(
                    parent,
                    current,
                    PathComparison))
                throw new DirectoryNotFoundException(
                    $"{label} has no existing physical ancestor.");

            current = parent;
        }

        while (true)
        {
            EnsurePhysicalDirectory(current, label);

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent)
                || string.Equals(
                    parent,
                    current,
                    PathComparison))
                break;

            current = parent;
        }
    }

    private static void EnsurePhysicalDirectory(
        string path,
        string label)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0)
            throw new InvalidDataException(
                $"{label} must contain only directories.");

        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException(
                $"{label} cannot pass through symbolic links, junctions or other reparse points.");
    }

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
