using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class ManagedRuntimeArchiveRegression
{
    internal static Task RunAsync()
    {
        TestZipNormalAndTraversal();
        TestTarNormalAndLinkEscape();
        TestPreexistingLinkedPath();
        TestInternalTarSymlink();
        return Task.CompletedTask;
    }

    private static void TestZipNormalAndTraversal()
    {
        var root = NewRoot("zip");
        try
        {
            var archivePath = Path.Combine(root, "runtime.zip");
            using (var file = File.Create(archivePath))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                var normal = archive.CreateEntry("jdk/bin/java");
                using (var output = normal.Open())
                {
                    var bytes = Encoding.UTF8.GetBytes("java");
                    output.Write(bytes);
                }

                var traversal = archive.CreateEntry("../outside.txt");
                using (var output = traversal.Open())
                {
                    var bytes = Encoding.UTF8.GetBytes("escape");
                    output.Write(bytes);
                }
            }

            var destination = Path.Combine(root, "staging");
            var outside = Path.Combine(root, "outside.txt");
            ExpectInvalid(
                () => JavaRuntimeProvisionService.ExtractArchive(archivePath, destination),
                "ZIP traversal entry");
            Assert(!File.Exists(outside),
                "ZIP traversal must not create a file outside staging.");
        }
        finally
        {
            TryDelete(root);
        }

        var validRoot = NewRoot("zip-valid");
        try
        {
            var archivePath = Path.Combine(validRoot, "runtime.zip");
            using (var file = File.Create(archivePath))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                var normal = archive.CreateEntry("jdk/bin/java");
                using var output = normal.Open();
                var bytes = Encoding.UTF8.GetBytes("java");
                output.Write(bytes);
            }

            var destination = Path.Combine(validRoot, "staging");
            JavaRuntimeProvisionService.ExtractArchive(archivePath, destination);
            Assert(
                File.ReadAllText(Path.Combine(destination, "jdk", "bin", "java")) == "java",
                "Normal nested ZIP file should extract inside staging.");
        }
        finally
        {
            TryDelete(validRoot);
        }
    }

    private static void TestTarNormalAndLinkEscape()
    {
        var root = NewRoot("tar");
        try
        {
            var normalArchive = Path.Combine(root, "normal.tar.gz");
            CreateTarGz(normalArchive, writer =>
            {
                WriteTarFile(writer, "jdk/bin/java", "java");
            });
            var normalDestination = Path.Combine(root, "normal-staging");
            JavaRuntimeProvisionService.ExtractArchive(normalArchive, normalDestination);
            Assert(
                File.ReadAllText(Path.Combine(normalDestination, "jdk", "bin", "java")) == "java",
                "Normal nested TAR file should extract inside staging.");

            var maliciousArchive = Path.Combine(root, "link-escape.tar.gz");
            CreateTarGz(maliciousArchive, writer =>
            {
                var link = new PaxTarEntry(TarEntryType.SymbolicLink, "jdk/lib/escape")
                {
                    LinkName = "../../../outside-target"
                };
                writer.WriteEntry(link);
            });

            var outside = Path.Combine(root, "outside-target");
            var maliciousDestination = Path.Combine(root, "malicious-staging");
            ExpectInvalid(
                () => JavaRuntimeProvisionService.ExtractArchive(
                    maliciousArchive,
                    maliciousDestination),
                "TAR symlink escape");
            Assert(!File.Exists(outside) && !Directory.Exists(outside),
                "Escaping TAR symlink must not create or modify its outside target.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void TestPreexistingLinkedPath()
    {
        var root = NewRoot("prelinked");
        try
        {
            var outside = Path.Combine(root, "outside");
            var destination = Path.Combine(root, "staging");
            Directory.CreateDirectory(outside);
            Directory.CreateDirectory(destination);
            var linked = Path.Combine(destination, "linked");
            if (!TryCreateDirectorySymlink(linked, outside))
            {
                Console.WriteLine("SKIP managed Java pre-existing symlink regression: runner cannot create directory symlinks.");
                return;
            }

            var archivePath = Path.Combine(root, "runtime.zip");
            using (var file = File.Create(archivePath))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("linked/pwn.txt");
                using var output = entry.Open();
                var bytes = Encoding.UTF8.GetBytes("pwn");
                output.Write(bytes);
            }

            ExpectInvalid(
                () => JavaRuntimeProvisionService.ExtractArchive(archivePath, destination),
                "pre-existing linked path component");
            Assert(!File.Exists(Path.Combine(outside, "pwn.txt")),
                "Extraction must not write through a pre-existing staging symlink.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void TestInternalTarSymlink()
    {
        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP managed Java internal TAR symlink regression on Windows.");
            return;
        }

        var root = NewRoot("internal-link");
        try
        {
            var archivePath = Path.Combine(root, "runtime.tar.gz");
            CreateTarGz(archivePath, writer =>
            {
                WriteTarFile(writer, "jdk/lib/target.txt", "target");
                var link = new PaxTarEntry(TarEntryType.SymbolicLink, "jdk/lib/link.txt")
                {
                    LinkName = "target.txt"
                };
                writer.WriteEntry(link);
            });

            var destination = Path.Combine(root, "staging");
            JavaRuntimeProvisionService.ExtractArchive(archivePath, destination);
            var linkPath = Path.Combine(destination, "jdk", "lib", "link.txt");
            Assert(
                (File.GetAttributes(linkPath) & FileAttributes.ReparsePoint) != 0,
                "Safe relative TAR symlink should be retained on Unix.");
            Assert(File.ReadAllText(linkPath) == "target",
                "Safe internal TAR symlink should resolve to the staged target.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void CreateTarGz(string path, Action<TarWriter> write)
    {
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.SmallestSize);
        using var writer = new TarWriter(gzip, leaveOpen: false);
        write(writer);
    }

    private static void WriteTarFile(TarWriter writer, string name, string content)
    {
        using var data = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = data
        };
        writer.WriteEntry(entry);
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

    private static string NewRoot(string name)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-runtime-archive-tests",
            name + "-" + Guid.NewGuid().ToString("N"));
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
