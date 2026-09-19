namespace UN.Nexo.Core.Services;

internal static class InstallStateSnapshot
{
    internal const long MaxBytes = 1024L * 1024L;
    private const string FileName = "install-state.json";

    internal static async Task<byte[]?> ReadAsync(
        string instanceRoot,
        CancellationToken cancellationToken)
    {
        var root = RequirePhysicalInstanceRoot(instanceRoot);
        var path = GetStatePath(root);

        if (Directory.Exists(path))
        {
            throw new InvalidDataException(
                "Install state path is occupied by a directory.");
        }

        if (!File.Exists(path))
            return null;

        RejectReparseFile(path);
        var info = new FileInfo(path);
        if (info.Length > MaxBytes)
        {
            throw new InvalidDataException(
                $"Install state exceeds the {MaxBytes}-byte rollback snapshot limit.");
        }

        RequirePhysicalInstanceRoot(root);
        RejectReparseFile(path);

        await using var stream =
            new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous
                | FileOptions.SequentialScan);

        RequirePhysicalInstanceRoot(root);
        RejectReparseFile(path);

        if (stream.Length > MaxBytes)
        {
            throw new InvalidDataException(
                $"Install state exceeds the {MaxBytes}-byte rollback snapshot limit.");
        }

        var length =
            checked((int)stream.Length);
        var bytes =
            new byte[length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read =
                await stream.ReadAsync(
                    bytes.AsMemory(offset),
                    cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    "Install state changed while it was being snapshotted.");
            }

            offset += read;
        }

        RequirePhysicalInstanceRoot(root);
        RejectReparseFile(path);
        return bytes;
    }

    internal static void Delete(
        string instanceRoot)
    {
        var root = RequirePhysicalInstanceRoot(instanceRoot);
        var path = GetStatePath(root);

        if (Directory.Exists(path))
        {
            throw new InvalidDataException(
                "Install state path is occupied by a directory.");
        }

        if (!File.Exists(path))
            return;

        RejectReparseFile(path);
        RequirePhysicalInstanceRoot(root);
        File.Delete(path);
        RequirePhysicalInstanceRoot(root);
    }

    internal static async Task RestoreAsync(
        string instanceRoot,
        byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.LongLength > MaxBytes)
        {
            throw new InvalidDataException(
                $"Install state exceeds the {MaxBytes}-byte rollback snapshot limit.");
        }

        var root = RequirePhysicalInstanceRoot(instanceRoot);
        var path = GetStatePath(root);

        if (Directory.Exists(path))
        {
            throw new InvalidDataException(
                "Install state path is occupied by a directory.");
        }

        RejectReparseFileIfPresent(path);

        var temp =
            path + "."
            + Guid.NewGuid().ToString("N")
            + ".restore";

        try
        {
            RejectReparseFileIfPresent(temp);
            await using (var output =
                         new FileStream(
                             temp,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous
                             | FileOptions.WriteThrough))
            {
                await output.WriteAsync(
                    bytes,
                    CancellationToken.None);
                await output.FlushAsync(
                    CancellationToken.None);
            }

            RequirePhysicalInstanceRoot(root);
            RejectReparseFileIfPresent(path);
            RejectReparseFileIfPresent(temp);

            File.Move(
                temp,
                path,
                overwrite: true);

            RequirePhysicalInstanceRoot(root);
            RejectReparseFile(path);
        }
        finally
        {
            TryDeleteTemp(root, temp);
        }
    }

    private static string GetStatePath(
        string root)
    {
        var path =
            Path.GetFullPath(
                Path.Combine(
                    root,
                    FileName));
        var parent =
            Path.GetDirectoryName(path)
            ?? throw new InvalidDataException(
                "Install state has no parent directory.");
        var comparison =
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        if (!Path.GetFullPath(parent)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar)
                .Equals(
                    root.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar),
                    comparison))
        {
            throw new InvalidDataException(
                "Install state escaped the managed instance directory.");
        }

        return path;
    }

    private static string RequirePhysicalInstanceRoot(
        string path)
    {
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException(
                $"Managed instance directory is missing: {full}");
        }

        var attributes =
            File.GetAttributes(full);
        if ((attributes & FileAttributes.Directory) == 0
            || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "Managed instance directory must be a physical non-reparse directory.");
        }

        return full;
    }

    private static void RejectReparseFile(
        string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "Install state disappeared during the operation.",
                path);
        }

        var attributes =
            File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) != 0
            || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "Install state must be a regular non-reparse file.");
        }
    }

    private static void RejectReparseFileIfPresent(
        string path)
    {
        if (!File.Exists(path)
            && !Directory.Exists(path))
        {
            return;
        }

        var attributes =
            File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) != 0
            || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "Install state path must be a regular non-reparse file.");
        }
    }

    private static void TryDeleteTemp(
        string root,
        string temp)
    {
        try
        {
            RequirePhysicalInstanceRoot(root);
            if (!File.Exists(temp))
                return;

            var attributes =
                File.GetAttributes(temp);
            if ((attributes & FileAttributes.Directory) == 0
                && (attributes & FileAttributes.ReparsePoint) == 0)
            {
                File.Delete(temp);
            }
        }
        catch
        {
            // Fail closed during cleanup if the managed parent changed.
        }
    }
}
