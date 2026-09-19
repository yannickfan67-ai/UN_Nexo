using System.Diagnostics;
using System.Reflection;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Fabric.Tests;

internal static class InstanceModServiceRegression
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "un-nexo-mod-tests-" + Guid.NewGuid().ToString("N"));
        var sourceRoot = Path.Combine(root, "sources");
        Directory.CreateDirectory(sourceRoot);
        try
        {
            var paths = new NexoPathService(root);
            var service = new InstanceModService(paths);
            var primaryId = Guid.NewGuid().ToString("N");
            var otherId = Guid.NewGuid().ToString("N");
            var linkedId = Guid.NewGuid().ToString("N");
            var source = Path.Combine(sourceRoot, "example-mod.jar");
            await File.WriteAllBytesAsync(source, [1, 2, 3]);
            var installed = await service.InstallAsync(primaryId, source);
            Assert(installed.IsEnabled, "Freshly installed mod should be enabled.");
            Assert(installed.FileName == "example-mod.jar", "Installed mod filename changed unexpectedly.");
            Assert(File.Exists(Path.Combine(paths.GetInstanceGameDirectory(primaryId), "mods", "example-mod.jar")), "Mod was not copied into the selected instance.");
            Assert(service.List(otherId).Count == 0, "Mods leaked into another instance.");
            var disabled = await service.SetEnabledAsync(primaryId, installed.FileName, false);
            Assert(!disabled.IsEnabled && disabled.FileName == "example-mod.jar.disabled", "Disabling should rename the JAR to .jar.disabled.");
            Assert(!service.List(primaryId).Single().IsEnabled, "Disabled state was not listed correctly.");
            await File.WriteAllBytesAsync(source, [9, 8, 7, 6]);
            var replaced = await service.InstallAsync(primaryId, source, true);
            Assert(replaced.IsEnabled, "Replacing a disabled mod should re-enable the newly installed JAR.");
            Assert(service.List(primaryId).Count == 1, "Replacing a disabled mod must not leave duplicate entries.");
            Assert((await File.ReadAllBytesAsync(Path.Combine(paths.GetInstanceGameDirectory(primaryId), "mods", "example-mod.jar"))).SequenceEqual(new byte[] { 9, 8, 7, 6 }), "Replacement did not publish the new JAR content.");
            try { await service.InstallAsync(primaryId, source, false); throw new Exception("Duplicate install without replacement should fail."); } catch (IOException) { }
            await service.RemoveAsync(primaryId, replaced.FileName);
            Assert(service.List(primaryId).Count == 0, "Removing a mod should remove it from the instance.");
            var textFile = Path.Combine(sourceRoot, "not-a-mod.txt");
            await File.WriteAllTextAsync(textFile, "not a jar");
            try { await service.InstallAsync(primaryId, textFile); throw new Exception("Non-JAR mod installation should fail."); } catch (InvalidDataException) { }
            try { await service.RemoveAsync(primaryId, "../escape.jar"); throw new Exception("Traversal mod filename should fail."); } catch (InvalidDataException) { }

            await VerifyMutationsWaitForInstanceLeaseAsync(paths, service, source);
            await VerifyCrossProcessLeaseBlocksInstallAsync(paths, service, source, root);
            await VerifyLinkedModsDirectoryIsRejectedAsync(paths, service, source, root, linkedId);
            await VerifyLinkedManagedEntriesAreRejectedAsync(paths, service);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static async Task VerifyMutationsWaitForInstanceLeaseAsync(
        NexoPathService paths,
        InstanceModService service,
        string source)
    {
        var instanceId = Guid.NewGuid().ToString("N");
        var installed = await service.InstallAsync(instanceId, source);
        var coordinator = new InstanceOperationCoordinator(paths);

        var lease = await coordinator.AcquireAsync(instanceId, "test-holder");
        Task<InstalledMod>? disable = null;
        try
        {
            disable = service.SetEnabledAsync(instanceId, installed.FileName, false);
            await Task.Delay(150);
            Assert(!disable.IsCompleted, "Disabling a mod must wait for the instance operation lease.");
        }
        finally
        {
            await lease.DisposeAsync();
        }

        var disabled = await disable.WaitAsync(TimeSpan.FromSeconds(5));

        lease = await coordinator.AcquireAsync(instanceId, "test-holder");
        Task? remove = null;
        try
        {
            remove = service.RemoveAsync(instanceId, disabled.FileName);
            await Task.Delay(150);
            Assert(!remove.IsCompleted, "Removing a mod must wait for the instance operation lease.");
        }
        finally
        {
            await lease.DisposeAsync();
        }

        await remove.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task VerifyCrossProcessLeaseBlocksInstallAsync(
        NexoPathService paths,
        InstanceModService service,
        string source,
        string root)
    {
        var instanceId = Guid.NewGuid().ToString("N");
        var ready = Path.Combine(root, "cross-process-ready-" + instanceId);
        var release = Path.Combine(root, "cross-process-release-" + instanceId);
        using var helper = StartLeaseHelper(root, instanceId, ready, release);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        while (!File.Exists(ready))
        {
            if (helper.HasExited)
            {
                var stderr = await helper.StandardError.ReadToEndAsync();
                throw new Exception("Cross-process lease helper exited early: " + stderr);
            }
            await Task.Delay(25, timeout.Token);
        }

        var install = service.InstallAsync(
            instanceId,
            source,
            cancellationToken: timeout.Token);
        await Task.Delay(200, timeout.Token);

        Assert(!install.IsCompleted, "Mod installation must wait while another Nexo process holds the instance lease.");
        Assert(!Directory.Exists(paths.GetInstanceGameDirectory(instanceId)),
            "Blocked mod installation must not mutate the instance before acquiring the lease.");

        await File.WriteAllTextAsync(release, "release", timeout.Token);
        await install.WaitAsync(timeout.Token);
        await helper.WaitForExitAsync(timeout.Token);
        Assert(helper.ExitCode == 0, "Cross-process lease helper should exit cleanly.");
    }

    private static Process StartLeaseHelper(
        string root,
        string instanceId,
        string ready,
        string release)
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not determine the current test host.");
        var entryAssembly = Assembly.GetEntryAssembly()?.Location
            ?? throw new InvalidOperationException("Could not determine the Fabric test assembly.");

        var start = new ProcessStartInfo(processPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (Path.GetFileNameWithoutExtension(processPath)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(entryAssembly);

        start.ArgumentList.Add("--hold-instance-lease");
        start.ArgumentList.Add(root);
        start.ArgumentList.Add(instanceId);
        start.ArgumentList.Add(ready);
        start.ArgumentList.Add(release);

        return Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the cross-process lease helper.");
    }

    private static async Task VerifyLinkedModsDirectoryIsRejectedAsync(
        NexoPathService paths,
        InstanceModService service,
        string source,
        string root,
        string instanceId)
    {
        var game = paths.GetInstanceGameDirectory(instanceId);
        var mods = Path.Combine(game, "mods");
        var outside = Path.Combine(root, "outside-mods");
        Directory.CreateDirectory(game);
        Directory.CreateDirectory(outside);
        try { Directory.CreateSymbolicLink(mods, outside); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException) { return; }

        try { await service.InstallAsync(instanceId, source); throw new Exception("Installing through a linked mods directory should fail."); } catch (InvalidDataException) { }
        Assert(!File.Exists(Path.Combine(outside, Path.GetFileName(source))), "Linked mods install wrote outside the instance.");
        try { service.List(instanceId); throw new Exception("Listing through a linked mods directory should fail closed."); } catch (InvalidDataException) { }
    }

    private static async Task VerifyLinkedManagedEntriesAreRejectedAsync(
        NexoPathService paths,
        InstanceModService service)
    {
        var externalRoot = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-mod-link-target-"
            + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(externalRoot);

        var sentinel = Path.Combine(externalRoot, "sentinel.jar");
        var sentinelBytes = new byte[] { 4, 2, 4, 2 };
        await File.WriteAllBytesAsync(sentinel, sentinelBytes);

        try
        {
            var disableId = Guid.NewGuid().ToString("N");
            var disableMods = Path.Combine(
                paths.GetInstanceGameDirectory(disableId),
                "mods");
            Directory.CreateDirectory(disableMods);
            var linkedEnabled = Path.Combine(disableMods, "linked.jar");
            if (!TryCreateFileLink(linkedEnabled, sentinel))
            {
                Console.WriteLine(
                    "SKIP linked managed-mod regression: platform denied file symlink creation");
                return;
            }

            try
            {
                await service.SetEnabledAsync(
                    disableId,
                    "linked.jar",
                    enabled: false);
                throw new Exception(
                    "Disabling a linked managed JAR should fail closed.");
            }
            catch (InvalidDataException)
            {
            }

            Assert(
                !File.Exists(Path.Combine(disableMods, "linked.jar.disabled")),
                "Rejecting a linked managed JAR must not publish a disabled entry.");
            Assert(
                (await File.ReadAllBytesAsync(sentinel)).SequenceEqual(sentinelBytes),
                "Disabling a linked managed JAR must not modify its external target.");

            var removeId = Guid.NewGuid().ToString("N");
            var removeMods = Path.Combine(
                paths.GetInstanceGameDirectory(removeId),
                "mods");
            Directory.CreateDirectory(removeMods);
            var linkedRemove = Path.Combine(removeMods, "remove.jar");
            Assert(
                TryCreateFileLink(linkedRemove, sentinel),
                "File symlink support changed during linked managed-mod regression.");

            try
            {
                await service.RemoveAsync(
                    removeId,
                    "remove.jar");
                throw new Exception(
                    "Removing a linked managed JAR should fail closed.");
            }
            catch (InvalidDataException)
            {
            }

            Assert(
                File.Exists(linkedRemove),
                "Rejecting removal should leave the linked managed entry untouched.");
            Assert(
                (await File.ReadAllBytesAsync(sentinel)).SequenceEqual(sentinelBytes),
                "Removing a linked managed JAR must not modify its external target.");

            var targetId = Guid.NewGuid().ToString("N");
            var targetMods = Path.Combine(
                paths.GetInstanceGameDirectory(targetId),
                "mods");
            Directory.CreateDirectory(targetMods);
            var physical = Path.Combine(targetMods, "target.jar");
            await File.WriteAllBytesAsync(physical, [1, 2, 3]);
            var linkedTarget = Path.Combine(targetMods, "target.jar.disabled");
            Assert(
                TryCreateFileLink(linkedTarget, sentinel),
                "File symlink support changed before target-path regression.");

            try
            {
                await service.SetEnabledAsync(
                    targetId,
                    "target.jar",
                    enabled: false);
                throw new Exception(
                    "Disabling onto a linked target entry should fail closed.");
            }
            catch (InvalidDataException)
            {
            }

            Assert(
                File.Exists(physical),
                "Rejecting a linked state target must preserve the physical source JAR.");
            Assert(
                (await File.ReadAllBytesAsync(sentinel)).SequenceEqual(sentinelBytes),
                "Rejecting a linked state target must not modify its external target.");
        }
        finally
        {
            try
            {
                Directory.Delete(externalRoot, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static bool TryCreateFileLink(
        string linkPath,
        string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
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

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
