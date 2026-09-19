using System.Runtime.CompilerServices;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class NexoPathRegression
{
    [ModuleInitializer]
    internal static void Run()
    {
        TestCanonicalInstanceIds();
        TestManagedRootsRejectLinks();
    }

    private static void TestCanonicalInstanceIds()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "nexo-path-regression",
            Guid.NewGuid().ToString("N"));
        var paths = new NexoPathService(root);
        var id = Guid.NewGuid().ToString("N");
        var resolved = paths.GetInstanceDirectory(id);
        var expected = Path.Combine(Path.GetFullPath(root), "instances", id);
        if (!resolved.Equals(
                expected,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new Exception(
                "Canonical instance ID did not resolve to one direct child of instances/.");
        }

        foreach (var invalid in new[]
                 {
                     "", " ", "foo", "foo/bar", "foo\\bar", "foo/../bar",
                     "./bar", ".", "..", id.ToUpperInvariant(), id + "/child"
                 })
        {
            try
            {
                _ = paths.GetInstanceDirectory(invalid);
                throw new Exception(
                    $"Non-canonical instance ID was accepted: {invalid}");
            }
            catch (ArgumentException)
            {
            }
        }

        var other = Guid.NewGuid().ToString("N");
        if (paths.GetInstanceDirectory(other).Equals(
                resolved,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new Exception(
                "Distinct canonical instance IDs resolved to the same path.");
        }
    }

    private static void TestManagedRootsRejectLinks()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "nexo-managed-root-regression",
            Guid.NewGuid().ToString("N"));
        var dataRoot = Path.Combine(root, "data");
        var externalInstances = Path.Combine(root, "external-instances");
        var externalRuntimes = Path.Combine(root, "external-runtimes");
        var externalData = Path.Combine(root, "external-data");
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(externalInstances);
        Directory.CreateDirectory(externalRuntimes);
        Directory.CreateDirectory(externalData);

        var instanceSentinel = Path.Combine(externalInstances, "keep.txt");
        var runtimeSentinel = Path.Combine(externalRuntimes, "keep.txt");
        var dataSentinel = Path.Combine(externalData, "keep.txt");
        File.WriteAllText(instanceSentinel, "keep");
        File.WriteAllText(runtimeSentinel, "keep");
        File.WriteAllText(dataSentinel, "keep");

        try
        {
            var paths = new NexoPathService(dataRoot);

            var instancesLink = Path.Combine(dataRoot, "instances");
            if (TryCreateDirectoryLink(instancesLink, externalInstances))
            {
                try
                {
                    ExpectInvalid(
                        paths.EnsureDirectories,
                        "linked instances root");
                    ExpectInvalid(
                        () => paths.GetInstanceDirectory(
                            Guid.NewGuid().ToString("N")),
                        "linked instances root lookup");
                    Assert(File.Exists(instanceSentinel),
                        "Rejecting a linked instances root must not modify its external target.");
                }
                finally
                {
                    TryDeleteLink(instancesLink);
                }
            }

            Directory.CreateDirectory(instancesLink);
            var runtimesLink = Path.Combine(dataRoot, "runtimes");
            if (TryCreateDirectoryLink(runtimesLink, externalRuntimes))
            {
                try
                {
                    ExpectInvalid(
                        paths.EnsureDirectories,
                        "linked runtimes root");
                    Assert(File.Exists(runtimeSentinel),
                        "Rejecting a linked runtimes root must not modify its external target.");
                }
                finally
                {
                    TryDeleteLink(runtimesLink);
                }
            }

            var linkedDataRoot = Path.Combine(root, "linked-data");
            if (TryCreateDirectoryLink(linkedDataRoot, externalData))
            {
                try
                {
                    var linkedPaths = new NexoPathService(linkedDataRoot);
                    ExpectInvalid(
                        linkedPaths.EnsureDirectories,
                        "linked data root");
                    Assert(File.Exists(dataSentinel),
                        "Rejecting a linked data root must not modify its external target.");
                }
                finally
                {
                    TryDeleteLink(linkedDataRoot);
                }
            }
        }
        finally
        {
            TryDeleteTree(root);
        }
    }

    private static bool TryCreateDirectoryLink(
        string linkPath,
        string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException
            or IOException
            or PlatformNotSupportedException
            or NotSupportedException)
        {
            return false;
        }
    }

    private static void TryDeleteLink(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path);
        }
        catch
        {
        }
    }

    private static void TryDeleteTree(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private static void ExpectInvalid(Action action, string scenario)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            return;
        }

        throw new Exception(
            $"Managed path safety did not reject {scenario}.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
