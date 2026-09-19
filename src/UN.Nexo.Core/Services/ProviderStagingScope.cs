namespace UN.Nexo.Core.Services;

internal sealed class ProviderStagingScope : IDisposable
{
    private readonly string _rootPath;
    private bool _disposed;

    private ProviderStagingScope(string rootPath)
    {
        _rootPath = Path.GetFullPath(rootPath);
        EnsurePhysicalDirectory(_rootPath);
    }

    internal string RootPath => _rootPath;

    internal static ProviderStagingScope Create()
    {
        var directory = Directory.CreateTempSubdirectory("UN_Nexo-provider-");
        return new ProviderStagingScope(directory.FullName);
    }

    internal string CreateEntryDirectory()
    {
        ThrowIfDisposed();
        EnsurePhysicalDirectory(_rootPath);

        var path = Path.Combine(
            _rootPath,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        EnsureContained(_rootPath, path);
        EnsurePhysicalDirectory(path);
        return path;
    }

    internal string ValidateStagedFile(
        string entryDirectory,
        string stagedPath)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(entryDirectory))
            throw new ArgumentException(
                "A provider staging directory is required.",
                nameof(entryDirectory));
        if (string.IsNullOrWhiteSpace(stagedPath))
            throw new InvalidDataException(
                "The provider returned an empty staged file path.");

        var fullEntry = Path.GetFullPath(entryDirectory);
        var fullPath = Path.GetFullPath(stagedPath);

        EnsurePhysicalDirectory(_rootPath);
        EnsureContained(_rootPath, fullEntry);
        EnsurePhysicalDirectory(fullEntry);
        EnsureContained(fullEntry, fullPath);
        EnsurePhysicalPathChain(fullEntry, fullPath);

        if (!File.Exists(fullPath))
            throw new FileNotFoundException(
                "The provider staged file does not exist.",
                fullPath);

        var attributes = File.GetAttributes(fullPath);
        if ((attributes & FileAttributes.Directory) != 0
            || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "The provider staged file must be a physical regular file inside the caller-owned staging directory.");
        }

        return fullPath;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            if (!Directory.Exists(_rootPath))
                return;
            if (!IsSafePhysicalTree(_rootPath))
                return;

            Directory.Delete(_rootPath, recursive: true);
        }
        catch
        {
            // Cleanup is best-effort. Refuse risky recursive deletion rather than
            // following a swapped link/reparse point outside the owned temp root.
        }
    }

    private static bool IsSafePhysicalTree(string rootPath)
    {
        try
        {
            EnsurePhysicalDirectory(rootPath);
            var stack = new Stack<string>();
            stack.Push(rootPath);

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                foreach (var entry in Directory.EnumerateFileSystemEntries(
                             current,
                             "*",
                             SearchOption.TopDirectoryOnly))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        return false;
                    if ((attributes & FileAttributes.Directory) != 0)
                        stack.Push(entry);
                }
            }

            // Revalidate immediately before the recursive delete.
            EnsurePhysicalDirectory(rootPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void EnsurePhysicalPathChain(
        string rootDirectory,
        string targetPath)
    {
        EnsureContained(rootDirectory, targetPath);

        var root = Path.GetFullPath(rootDirectory);
        var current = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        while (!string.IsNullOrEmpty(current))
        {
            EnsureContainedOrEqual(root, current);
            if (Directory.Exists(current))
                EnsurePhysicalDirectory(current);

            if (PathEquals(current, root))
                break;
            current = Path.GetDirectoryName(current);
        }
    }

    private static void EnsurePhysicalDirectory(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0
            || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "Provider staging directories must be physical non-reparse directories.");
        }
    }

    private static void EnsureContained(
        string rootDirectory,
        string candidatePath)
    {
        var root = Path.GetFullPath(rootDirectory);
        var candidate = Path.GetFullPath(candidatePath);
        var rootWithSeparator = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!candidate.StartsWith(rootWithSeparator, comparison))
            throw new InvalidDataException(
                "Provider staging paths must stay inside the caller-owned staging directory.");
    }

    private static void EnsureContainedOrEqual(
        string rootDirectory,
        string candidatePath)
    {
        if (PathEquals(rootDirectory, candidatePath))
            return;
        EnsureContained(rootDirectory, candidatePath);
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

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
