using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class InstallStateSnapshotRegression
{
    internal static async Task RunAsync()
    {
        await TestNormalSnapshotDeleteRestoreAsync();
        await TestOversizedStateRejectedAsync();
        await TestLinkedStateRejectedAsync();
    }

    private static async Task TestNormalSnapshotDeleteRestoreAsync()
    {
        var root = NewRoot("normal");
        try
        {
            var statePath =
                Path.Combine(
                    root,
                    "install-state.json");
            var expected =
                System.Text.Encoding.UTF8.GetBytes(
                    "{\"state\":\"previous\"}");
            await File.WriteAllBytesAsync(
                statePath,
                expected);

            var snapshot =
                await InstallStateSnapshot.ReadAsync(
                    root,
                    CancellationToken.None);
            Assert(
                snapshot is not null
                && snapshot.SequenceEqual(expected),
                "Install-state snapshot should retain a normal small prior state.");

            InstallStateSnapshot.Delete(root);
            Assert(
                !File.Exists(statePath),
                "Install-state delete should remove the regular prior state.");

            await InstallStateSnapshot.RestoreAsync(
                root,
                snapshot!);
            Assert(
                File.Exists(statePath),
                "Install-state restore should republish the prior state.");
            Assert(
                (await File.ReadAllBytesAsync(statePath))
                    .SequenceEqual(expected),
                "Restored install state bytes changed.");
        }
        finally
        {
            TryDeleteTree(root);
        }
    }

    private static async Task TestOversizedStateRejectedAsync()
    {
        var root = NewRoot("oversized");
        try
        {
            var statePath =
                Path.Combine(
                    root,
                    "install-state.json");
            await using (var stream =
                         new FileStream(
                             statePath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None))
            {
                stream.SetLength(
                    InstallStateSnapshot.MaxBytes
                    + 1);
            }

            try
            {
                await InstallStateSnapshot.ReadAsync(
                    root,
                    CancellationToken.None);
                throw new Exception(
                    "Oversized install state unexpectedly produced a rollback snapshot.");
            }
            catch (InvalidDataException)
            {
            }
        }
        finally
        {
            TryDeleteTree(root);
        }
    }

    private static async Task TestLinkedStateRejectedAsync()
    {
        var root = NewRoot("linked");
        var externalRoot = NewRoot("external");
        try
        {
            var sentinel =
                Path.Combine(
                    externalRoot,
                    "sentinel.json");
            var sentinelBytes =
                System.Text.Encoding.UTF8.GetBytes(
                    "{\"secret\":\"outside-instance\"}");
            await File.WriteAllBytesAsync(
                sentinel,
                sentinelBytes);

            var statePath =
                Path.Combine(
                    root,
                    "install-state.json");
            if (!TryCreateFileLink(
                    statePath,
                    sentinel))
            {
                Console.WriteLine(
                    "SKIP install-state file-link regression: platform denied symlink creation");
                return;
            }

            try
            {
                await InstallStateSnapshot.ReadAsync(
                    root,
                    CancellationToken.None);
                throw new Exception(
                    "Linked install state unexpectedly produced a rollback snapshot.");
            }
            catch (InvalidDataException)
            {
            }

            Assert(
                (await File.ReadAllBytesAsync(sentinel))
                    .SequenceEqual(sentinelBytes),
                "Rejecting a linked install state must not modify the external target.");
        }
        finally
        {
            TryDeleteFileLink(
                Path.Combine(
                    root,
                    "install-state.json"));
            TryDeleteTree(root);
            TryDeleteTree(externalRoot);
        }
    }

    private static string NewRoot(string label)
    {
        var root =
            Path.Combine(
                Path.GetTempPath(),
                "un-nexo-install-state-tests",
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

    private static void TryDeleteFileLink(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
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
                Directory.Delete(
                    path,
                    recursive: true);
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
}
