using System.Text.Json;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class InheritedLoaderProfileStoreRegression
{
    internal static async Task RunAsync()
    {
        await TestNormalWriteReadAsync();
        await TestOversizedProfileRejectedAsync();
        await TestLinkedProfileFileRejectedAsync();
        await TestLinkedVersionsDirectoryRejectedAsync();
        await TestLinkedProfileDirectoryRejectedAsync();
    }

    private static async Task TestNormalWriteReadAsync()
    {
        var fixture = CreateFixture("normal");
        try
        {
            using var document =
                JsonDocument.Parse(
                    """
                    {
                      "id":"fabric-loader-test",
                      "inheritsFrom":"1.21.4",
                      "libraries":[]
                    }
                    """);

            await InheritedLoaderProfileStore.WriteAsync(
                fixture.Paths,
                fixture.InstanceId,
                "fabric-loader-test",
                document.RootElement,
                CancellationToken.None);

            using var read =
                await InheritedLoaderProfileStore.ReadAsync(
                    fixture.Paths,
                    fixture.InstanceId,
                    "fabric-loader-test",
                    CancellationToken.None);

            Assert(
                read is not null,
                "Normal inherited loader profile should be readable.");
            Assert(
                read!.RootElement
                    .GetProperty("inheritsFrom")
                    .GetString()
                == "1.21.4",
                "Inherited loader profile content changed during publication.");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static async Task TestOversizedProfileRejectedAsync()
    {
        var fixture = CreateFixture("oversized");
        try
        {
            var resolved =
                InheritedLoaderProfileStore.Resolve(
                    fixture.Paths,
                    fixture.InstanceId,
                    "oversized-profile",
                    createDirectories: true);

            await using (var stream =
                         new FileStream(
                             resolved.ProfilePath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None))
            {
                stream.SetLength(
                    InheritedLoaderProfileStore.MaxProfileBytes
                    + 1);
            }

            try
            {
                using var _ =
                    await InheritedLoaderProfileStore.ReadAsync(
                        fixture.Paths,
                        fixture.InstanceId,
                        "oversized-profile",
                        CancellationToken.None);
                throw new Exception(
                    "Oversized inherited loader profile unexpectedly parsed.");
            }
            catch (InvalidDataException)
            {
            }
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static async Task TestLinkedProfileFileRejectedAsync()
    {
        var fixture = CreateFixture("linked-file");
        var externalRoot = NewExternalRoot("linked-file");
        try
        {
            var resolved =
                InheritedLoaderProfileStore.Resolve(
                    fixture.Paths,
                    fixture.InstanceId,
                    "linked-profile",
                    createDirectories: true);
            var external =
                Path.Combine(
                    externalRoot,
                    "outside.json");
            var sentinel =
                """
                {"id":"outside","inheritsFrom":"secret","libraries":[]}
                """;
            await File.WriteAllTextAsync(
                external,
                sentinel);

            if (!TryCreateFileLink(
                    resolved.ProfilePath,
                    external))
            {
                Console.WriteLine(
                    "SKIP inherited loader profile file-link regression: platform denied symlink creation");
                return;
            }

            try
            {
                using var _ =
                    await InheritedLoaderProfileStore.ReadAsync(
                        fixture.Paths,
                        fixture.InstanceId,
                        "linked-profile",
                        CancellationToken.None);
                throw new Exception(
                    "Linked inherited loader profile unexpectedly parsed.");
            }
            catch (InvalidDataException)
            {
            }

            Assert(
                await File.ReadAllTextAsync(external)
                == sentinel,
                "Rejecting a linked loader profile must not modify the external target.");
        }
        finally
        {
            TryDeleteTree(fixture.Root);
            TryDeleteTree(externalRoot);
        }
    }

    private static Task TestLinkedVersionsDirectoryRejectedAsync()
    {
        return TestLinkedDirectoryRejectedAsync(
            "linked-versions",
            linkProfileDirectory: false);
    }

    private static Task TestLinkedProfileDirectoryRejectedAsync()
    {
        return TestLinkedDirectoryRejectedAsync(
            "linked-profile-dir",
            linkProfileDirectory: true);
    }

    private static async Task TestLinkedDirectoryRejectedAsync(
        string label,
        bool linkProfileDirectory)
    {
        var fixture = CreateFixture(label);
        var externalRoot = NewExternalRoot(label);
        try
        {
            var instanceRoot =
                fixture.Paths.EnsureInstanceDirectoryPhysical(
                    fixture.InstanceId);
            var gameRoot =
                Path.Combine(
                    instanceRoot,
                    "game");
            Directory.CreateDirectory(gameRoot);

            string linkPath;
            if (linkProfileDirectory)
            {
                var versions =
                    Path.Combine(
                        gameRoot,
                        "versions");
                Directory.CreateDirectory(versions);
                linkPath =
                    Path.Combine(
                        versions,
                        "linked-profile-dir");
            }
            else
            {
                linkPath =
                    Path.Combine(
                        gameRoot,
                        "versions");
            }

            if (!TryCreateDirectoryLink(
                    linkPath,
                    externalRoot))
            {
                Console.WriteLine(
                    "SKIP inherited loader profile directory-link regression: platform denied symlink creation");
                return;
            }

            try
            {
                using var document =
                    JsonDocument.Parse(
                        """
                        {"id":"linked-profile-dir","inheritsFrom":"1.21.4","libraries":[]}
                        """);
                await InheritedLoaderProfileStore.WriteAsync(
                    fixture.Paths,
                    fixture.InstanceId,
                    "linked-profile-dir",
                    document.RootElement,
                    CancellationToken.None);
                throw new Exception(
                    "Linked inherited loader profile directory unexpectedly accepted publication.");
            }
            catch (InvalidDataException)
            {
            }

            Assert(
                !Directory.EnumerateFileSystemEntries(
                    externalRoot)
                    .Any(),
                "Loader profile publication must not write through a linked directory.");
        }
        finally
        {
            TryDeleteTree(fixture.Root);
            TryDeleteTree(externalRoot);
        }
    }

    private static Fixture CreateFixture(string label)
    {
        var root =
            Path.Combine(
                Path.GetTempPath(),
                "un-nexo-profile-store-tests",
                label + "-"
                + Guid.NewGuid().ToString("N"));
        var paths =
            new NexoPathService(root);
        paths.EnsureDirectories();

        var instanceId =
            Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(
            paths.GetInstanceDirectory(
                instanceId));

        return new Fixture(
            root,
            paths,
            instanceId);
    }

    private static string NewExternalRoot(string label)
    {
        var root =
            Path.Combine(
                Path.GetTempPath(),
                "un-nexo-profile-store-external",
                label + "-"
                + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static bool TryCreateFileLink(
        string linkPath,
        string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(
                linkPath,
                targetPath);
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

    private static bool TryCreateDirectoryLink(
        string linkPath,
        string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(
                linkPath,
                targetPath);
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

    private static void TryDeleteTree(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(
                    path,
                    recursive: true);
            }
        }
        catch
        {
        }
    }

    private static void Assert(
        bool condition,
        string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed record Fixture(
        string Root,
        NexoPathService Paths,
        string InstanceId) : IDisposable
    {
        public void Dispose()
            => TryDeleteTree(Root);
    }
}
