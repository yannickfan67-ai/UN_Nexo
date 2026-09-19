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
            var disabled = service.SetEnabled(primaryId, installed.FileName, false);
            Assert(!disabled.IsEnabled && disabled.FileName == "example-mod.jar.disabled", "Disabling should rename the JAR to .jar.disabled.");
            Assert(!service.List(primaryId).Single().IsEnabled, "Disabled state was not listed correctly.");
            await File.WriteAllBytesAsync(source, [9, 8, 7, 6]);
            var replaced = await service.InstallAsync(primaryId, source, true);
            Assert(replaced.IsEnabled, "Replacing a disabled mod should re-enable the newly installed JAR.");
            Assert(service.List(primaryId).Count == 1, "Replacing a disabled mod must not leave duplicate entries.");
            Assert((await File.ReadAllBytesAsync(Path.Combine(paths.GetInstanceGameDirectory(primaryId), "mods", "example-mod.jar"))).SequenceEqual(new byte[] { 9, 8, 7, 6 }), "Replacement did not publish the new JAR content.");
            try { await service.InstallAsync(primaryId, source, false); throw new Exception("Duplicate install without replacement should fail."); } catch (IOException) { }
            service.Remove(primaryId, replaced.FileName);
            Assert(service.List(primaryId).Count == 0, "Removing a mod should remove it from the instance.");
            var textFile = Path.Combine(sourceRoot, "not-a-mod.txt");
            await File.WriteAllTextAsync(textFile, "not a jar");
            try { await service.InstallAsync(primaryId, textFile); throw new Exception("Non-JAR mod installation should fail."); } catch (InvalidDataException) { }
            try { service.Remove(primaryId, "../escape.jar"); throw new Exception("Traversal mod filename should fail."); } catch (InvalidDataException) { }
            await VerifyLinkedModsDirectoryIsRejectedAsync(paths, service, source, root, linkedId);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
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

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
