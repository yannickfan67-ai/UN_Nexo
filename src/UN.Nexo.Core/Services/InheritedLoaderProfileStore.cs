using System.Text.Json;

namespace UN.Nexo.Core.Services;

internal sealed record InheritedLoaderProfilePath(
    string InstanceRoot,
    string GameRoot,
    string VersionsRoot,
    string ProfileRoot,
    string ProfilePath);

internal static class InheritedLoaderProfileStore
{
    internal const long MaxProfileBytes =
        8L * 1024L * 1024L;

    internal static InheritedLoaderProfilePath Resolve(
        NexoPathService paths,
        string instanceId,
        string versionId,
        bool createDirectories)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var component =
            MetadataPath.RequireSingleComponent(
                versionId,
                "loader profile version id");

        var instanceRoot =
            paths.EnsureInstanceDirectoryPhysical(
                instanceId);
        var gameRoot =
            EnsureDirectPhysicalChild(
                instanceRoot,
                "game",
                "instance game directory",
                createDirectories);
        var versionsRoot =
            EnsureDirectPhysicalChild(
                gameRoot,
                "versions",
                "instance versions directory",
                createDirectories);
        var profileRoot =
            EnsureDirectPhysicalChild(
                versionsRoot,
                component,
                "loader profile directory",
                createDirectories);

        var profilePath =
            MetadataPath.ResolveSingleComponent(
                profileRoot,
                component,
                ".json",
                "loader profile path");

        ValidatePhysicalChain(
            instanceRoot,
            gameRoot,
            versionsRoot,
            profileRoot);

        return new InheritedLoaderProfilePath(
            instanceRoot,
            gameRoot,
            versionsRoot,
            profileRoot,
            profilePath);
    }

    internal static async Task<JsonDocument?> ReadAsync(
        NexoPathService paths,
        string instanceId,
        string versionId,
        CancellationToken cancellationToken)
    {
        InheritedLoaderProfilePath resolved;
        try
        {
            resolved =
                Resolve(
                    paths,
                    instanceId,
                    versionId,
                    createDirectories: false);
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        if (Directory.Exists(resolved.ProfilePath))
        {
            throw new InvalidDataException(
                "Loader profile path is occupied by a directory.");
        }

        if (!File.Exists(resolved.ProfilePath))
            return null;

        RejectReparseFile(
            resolved.ProfilePath,
            "loader profile");
        ValidatePhysicalChain(
            resolved.InstanceRoot,
            resolved.GameRoot,
            resolved.VersionsRoot,
            resolved.ProfileRoot);

        var info =
            new FileInfo(resolved.ProfilePath);
        if (info.Length > MaxProfileBytes)
        {
            throw new InvalidDataException(
                "Loader profile exceeds the 8 MiB local metadata limit.");
        }

        await using var stream =
            new FileStream(
                resolved.ProfilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous
                | FileOptions.SequentialScan);

        if (stream.Length > MaxProfileBytes)
        {
            throw new InvalidDataException(
                "Loader profile exceeds the 8 MiB local metadata limit.");
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
                    "Loader profile changed while it was being read.");
            }

            offset += read;
        }

        ValidatePhysicalChain(
            resolved.InstanceRoot,
            resolved.GameRoot,
            resolved.VersionsRoot,
            resolved.ProfileRoot);
        RejectReparseFile(
            resolved.ProfilePath,
            "loader profile");

        return JsonDocument.Parse(bytes);
    }

    internal static async Task WriteAsync(
        NexoPathService paths,
        string instanceId,
        string versionId,
        JsonElement profile,
        CancellationToken cancellationToken)
    {
        var resolved =
            Resolve(
                paths,
                instanceId,
                versionId,
                createDirectories: true);

        if (Directory.Exists(resolved.ProfilePath))
        {
            throw new InvalidDataException(
                "Loader profile path is occupied by a directory.");
        }

        RejectReparseFileIfPresent(
            resolved.ProfilePath,
            "loader profile");

        var bytes =
            JsonSerializer.SerializeToUtf8Bytes(
                profile,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                });
        if (bytes.LongLength > MaxProfileBytes)
        {
            throw new InvalidDataException(
                "Loader profile exceeds the 8 MiB local metadata limit.");
        }

        var temp =
            resolved.ProfilePath
            + "."
            + Guid.NewGuid().ToString("N")
            + ".tmp";

        try
        {
            ValidatePhysicalChain(
                resolved.InstanceRoot,
                resolved.GameRoot,
                resolved.VersionsRoot,
                resolved.ProfileRoot);
            RejectReparseFileIfPresent(
                resolved.ProfilePath,
                "loader profile");
            RejectReparseFileIfPresent(
                temp,
                "loader profile temporary file");

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
                    cancellationToken);
                await output.FlushAsync(
                    cancellationToken);
            }

            ValidatePhysicalChain(
                resolved.InstanceRoot,
                resolved.GameRoot,
                resolved.VersionsRoot,
                resolved.ProfileRoot);
            RejectReparseFileIfPresent(
                resolved.ProfilePath,
                "loader profile");
            RejectReparseFile(
                temp,
                "loader profile temporary file");

            File.Move(
                temp,
                resolved.ProfilePath,
                overwrite: true);

            ValidatePhysicalChain(
                resolved.InstanceRoot,
                resolved.GameRoot,
                resolved.VersionsRoot,
                resolved.ProfileRoot);
            RejectReparseFile(
                resolved.ProfilePath,
                "loader profile");
        }
        finally
        {
            TryDeleteTemp(
                resolved,
                temp);
        }
    }

    private static string EnsureDirectPhysicalChild(
        string parent,
        string childName,
        string label,
        bool create)
    {
        RequirePhysicalDirectory(
            parent,
            "managed parent directory");

        var safeChild =
            MetadataPath.RequireSingleComponent(
                childName,
                label);
        var child =
            Path.GetFullPath(
                Path.Combine(
                    parent,
                    safeChild));
        var parentFull =
            Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(parent));
        var childParent =
            Path.GetDirectoryName(child);
        var comparison =
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        if (childParent is null
            || !Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(childParent))
                .Equals(
                    parentFull,
                    comparison))
        {
            throw new InvalidDataException(
                $"{label} escaped its managed parent.");
        }

        if (File.Exists(child)
            && !Directory.Exists(child))
        {
            throw new InvalidDataException(
                $"{label} is occupied by a file.");
        }

        if (!Directory.Exists(child))
        {
            if (!create)
            {
                throw new DirectoryNotFoundException(
                    $"{label} is missing: {child}");
            }

            RequirePhysicalDirectory(
                parent,
                "managed parent directory");
            Directory.CreateDirectory(child);
        }

        RequirePhysicalDirectory(
            child,
            label);
        RequirePhysicalDirectory(
            parent,
            "managed parent directory");
        return child;
    }

    private static void ValidatePhysicalChain(
        string instanceRoot,
        string gameRoot,
        string versionsRoot,
        string profileRoot)
    {
        RequirePhysicalDirectory(
            instanceRoot,
            "managed instance directory");
        RequirePhysicalDirectory(
            gameRoot,
            "instance game directory");
        RequirePhysicalDirectory(
            versionsRoot,
            "instance versions directory");
        RequirePhysicalDirectory(
            profileRoot,
            "loader profile directory");
    }

    private static void RequirePhysicalDirectory(
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
        if ((attributes & FileAttributes.Directory) == 0
            || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"{label} must be a physical non-reparse directory.");
        }
    }

    private static void RejectReparseFile(
        string path,
        string label)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"{label} disappeared during the operation.",
                path);
        }

        var attributes =
            File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) != 0
            || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"{label} must be a regular non-reparse file.");
        }
    }

    private static void RejectReparseFileIfPresent(
        string path,
        string label)
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
                $"{label} must be a regular non-reparse file.");
        }
    }

    private static void TryDeleteTemp(
        InheritedLoaderProfilePath resolved,
        string temp)
    {
        try
        {
            ValidatePhysicalChain(
                resolved.InstanceRoot,
                resolved.GameRoot,
                resolved.VersionsRoot,
                resolved.ProfileRoot);
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
            // Fail closed if the managed path changed during cleanup.
        }
    }
}
