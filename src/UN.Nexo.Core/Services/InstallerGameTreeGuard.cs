namespace UN.Nexo.Core.Services;

internal static class InstallerGameTreeGuard
{
    private static readonly string[] WritableDirectoryRoots =
    [
        "libraries",
        "versions"
    ];

    private static readonly string[] WritableRootFiles =
    [
        "launcher_profiles.json",
        "launcher_profiles_microsoft_store.json"
    ];

    internal static void Validate(string gameRoot, string loaderName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(loaderName);

        var root = Path.GetFullPath(gameRoot);
        RequirePhysicalDirectory(root, loaderName + " game root");

        foreach (var directoryName in WritableDirectoryRoots)
        {
            var directory = Path.Combine(root, directoryName);
            if (File.Exists(directory) && !Directory.Exists(directory))
            {
                throw new InvalidDataException(
                    $"{loaderName} installer-owned directory '{directoryName}' is occupied by a file.");
            }

            if (!Directory.Exists(directory))
                continue;

            ValidatePhysicalTree(
                directory,
                $"{loaderName} installer-owned '{directoryName}' tree");
            RequirePhysicalDirectory(root, loaderName + " game root");
        }

        foreach (var fileName in WritableRootFiles)
        {
            var path = Path.Combine(root, fileName);
            if (!File.Exists(path) && !Directory.Exists(path))
                continue;

            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"{loaderName} installer-owned file '{fileName}' must not be a symbolic link, junction or other reparse point.");
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                throw new InvalidDataException(
                    $"{loaderName} installer-owned file '{fileName}' is occupied by a directory.");
            }
        }

        RequirePhysicalDirectory(root, loaderName + " game root");
    }

    private static void ValidatePhysicalTree(
        string treeRoot,
        string label)
    {
        RequirePhysicalDirectory(treeRoot, label);

        var pending = new Stack<string>();
        pending.Push(treeRoot);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            RequirePhysicalDirectory(current, label);

            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        $"{label} contains a symbolic link, junction or other reparse point: {entry}");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(entry);
            }

            RequirePhysicalDirectory(current, label);
        }

        RequirePhysicalDirectory(treeRoot, label);
    }

    private static void RequirePhysicalDirectory(
        string path,
        string label)
    {
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"{label} is missing: {path}");

        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0
            || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"{label} must be a physical non-reparse directory.");
        }
    }
}
