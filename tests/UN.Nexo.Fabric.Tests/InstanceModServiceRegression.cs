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
            var source = Path.Combine(sourceRoot, "example-mod.jar");
            await File.WriteAllBytesAsync(source, [1, 2, 3]);

            var installed = await service.InstallAsync("fabric-a", source);
            Assert(installed.IsEnabled, "Freshly installed mod should be enabled.");
            Assert(installed.FileName == "example-mod.jar", "Installed mod filename changed unexpectedly.");
            Assert(File.Exists(Path.Combine(paths.GetInstanceGameDirectory("fabric-a"), "mods", "example-mod.jar")), "Mod was not copied into the selected instance.");
            Assert(service.List("fabric-b").Count == 0, "Mods leaked into another instance.");

            var disabled = service.SetEnabled("fabric-a", installed.FileName, enabled: false);
            Assert(!disabled.IsEnabled && disabled.FileName == "example-mod.jar.disabled", "Disabling should rename the JAR to .jar.disabled.");
            Assert(service.List("fabric-a").Single().IsEnabled == false, "Disabled state was not listed correctly.");

            await File.WriteAllBytesAsync(source, [9, 8, 7, 6]);
            var replaced = await service.InstallAsync("fabric-a", source, replaceExisting: true);
            Assert(replaced.IsEnabled, "Replacing a disabled mod should re-enable the newly installed JAR.");
            Assert(service.List("fabric-a").Count == 1, "Replacing a disabled mod must not leave duplicate enabled/disabled entries.");
            Assert((await File.ReadAllBytesAsync(Path.Combine(paths.GetInstanceGameDirectory("fabric-a"), "mods", "example-mod.jar"))).SequenceEqual(new byte[] { 9, 8, 7, 6 }), "Replacement did not publish the new JAR content.");

            try { await service.InstallAsync("fabric-a", source, replaceExisting: false); throw new Exception("Duplicate install without replacement should fail."); }
            catch (IOException) { }

            service.Remove("fabric-a", replaced.FileName);
            Assert(service.List("fabric-a").Count == 0, "Removing a mod should remove it from the instance.");

            var textFile = Path.Combine(sourceRoot, "not-a-mod.txt");
            await File.WriteAllTextAsync(textFile, "not a jar");
            try { await service.InstallAsync("fabric-a", textFile); throw new Exception("Non-JAR mod installation should fail."); }
            catch (InvalidDataException) { }

            try { service.Remove("fabric-a", "../escape.jar"); throw new Exception("Traversal mod filename should fail."); }
            catch (InvalidDataException) { }

            await VerifyLinkedModsDirectoryIsRejectedAsync(paths, service, source, root);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task VerifyLinkedModsDirectoryIsRejectedAsync(NexoPathService paths, InstanceModService service, string source, string root)
    {
        var game = paths.GetInstanceGameDirectory("fabric-linked");
        var mods = Path.Combine(game, "mods");
        var outside = Path.Combine(root, "outside-mods");
        Directory.CreateDirectory(game);
        Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateSymbolicLink(mods, outside);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            return;
        }

        try
        {
            await service.InstallAsync("fabric-linked", source);
            throw new Exception("Installing through a linked mods directory should fail.");
        }
        catch (InvalidDataException) { }

        Assert(!File.Exists(Path.Combine(outside, Path.GetFileName(source))), "Linked mods install wrote outside the instance.");
        Assert(service.List("fabric-linked").Count == 0, "Listing through a linked mods directory should not expose outside files.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
