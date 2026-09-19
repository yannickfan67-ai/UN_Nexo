using System.IO.Compression;
using System.Text;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class NativeExtractionRegression
{
    internal static Task RunAsync()
    {
        TestNormalNestedEntry();
        TestSecondExtractionRemovesStaleFiles();
        TestTraversalLeavesNoPartialTree();
        TestPreexistingLinkedComponent();
        return Task.CompletedTask;
    }

    private static void TestNormalNestedEntry()
    {
        var root = NewRoot("normal");
        try
        {
            var archive = Path.Combine(root, "native.zip");
            CreateZip(archive, ("nested/native.bin", "payload"));
            var target = Path.Combine(root, "natives", "test");

            MinecraftVanillaInstallService.ExtractNativeArchive(
                archive,
                target,
                ["META-INF/"]);

            Assert(
                File.ReadAllText(Path.Combine(target, "nested", "native.bin")) == "payload",
                "Safe nested native entry should extract inside the natives root.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void TestSecondExtractionRemovesStaleFiles()
    {
        var root = NewRoot("stale");
        try
        {
            var first = Path.Combine(root, "first.zip");
            var second = Path.Combine(root, "second.zip");
            CreateZip(
                first,
                ("keep/native.bin", "first"),
                ("old/stale.bin", "stale"));
            CreateZip(
                second,
                ("keep/native.bin", "second"));

            var target = Path.Combine(
                root,
                "natives",
                "test");
            MinecraftVanillaInstallService.ExtractNativeArchive(
                first,
                target,
                ["META-INF/"]);
            Assert(
                File.Exists(Path.Combine(
                    target,
                    "old",
                    "stale.bin")),
                "First extraction should create the old native fixture.");

            MinecraftVanillaInstallService.ExtractNativeArchive(
                second,
                target,
                ["META-INF/"]);

            Assert(
                !File.Exists(Path.Combine(
                    target,
                    "old",
                    "stale.bin")),
                "Clean native publication must remove files no longer present in the verified archive set.");
            Assert(
                File.ReadAllText(Path.Combine(
                    target,
                    "keep",
                    "native.bin")) == "second",
                "Second clean publication should replace retained native content.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void TestTraversalLeavesNoPartialTree()
    {
        var root = NewRoot("traversal");
        try
        {
            var archive = Path.Combine(root, "native.zip");
            CreateZip(
                archive,
                ("safe/native.bin", "safe"),
                ("../outside.bin", "escape"));

            var target = Path.Combine(root, "natives", "test");
            var outside = Path.Combine(root, "natives", "outside.bin");
            ExpectInvalid(
                () => MinecraftVanillaInstallService.ExtractNativeArchive(
                    archive,
                    target,
                    ["META-INF/"]),
                "native traversal");

            Assert(!File.Exists(outside),
                "Traversal entry must not write outside the version natives root.");
            Assert(!File.Exists(Path.Combine(target, "safe", "native.bin")),
                "Failed archive validation must not partially publish earlier safe entries.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void TestPreexistingLinkedComponent()
    {
        var root = NewRoot("linked");
        try
        {
            var target = Path.Combine(root, "natives", "test");
            var outside = Path.Combine(root, "outside");
            Directory.CreateDirectory(target);
            Directory.CreateDirectory(outside);
            var linked = Path.Combine(target, "redirect");
            if (!TryCreateDirectorySymlink(linked, outside))
            {
                Console.WriteLine("SKIP native linked-path regression: runner cannot create directory symlinks.");
                return;
            }

            var archive = Path.Combine(root, "native.zip");
            CreateZip(
                archive,
                ("safe/native.bin", "safe"),
                ("redirect/payload.bin", "escape"));

            ExpectInvalid(
                () => MinecraftVanillaInstallService.ExtractNativeArchive(
                    archive,
                    target,
                    ["META-INF/"]),
                "pre-existing native symlink component");

            Assert(!File.Exists(Path.Combine(outside, "payload.bin")),
                "Native extraction must not write through a pre-existing symlink/junction.");
            Assert(!File.Exists(Path.Combine(target, "safe", "native.bin")),
                "Linked-path rejection must occur before any staged native tree is published.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void CreateZip(
        string path,
        params (string Name, string Content)[] entries)
    {
        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var item in entries)
        {
            var entry = archive.CreateEntry(item.Name);
            using var output = entry.Open();
            var bytes = Encoding.UTF8.GetBytes(item.Content);
            output.Write(bytes);
        }
    }

    private static bool TryCreateDirectorySymlink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (
            ex is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException
            or NotSupportedException)
        {
            return false;
        }
    }

    private static void ExpectInvalid(Action action, string label)
    {
        try
        {
            action();
            throw new Exception(label + " should have been rejected.");
        }
        catch (InvalidDataException)
        {
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private static string NewRoot(string suffix)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-native-extract-tests",
            suffix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string path)
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
}
