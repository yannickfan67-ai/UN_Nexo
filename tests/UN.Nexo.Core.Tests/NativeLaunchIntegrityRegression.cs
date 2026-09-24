using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UN.Nexo.Core.Launching;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class NativeLaunchIntegrityRegression
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-native-launch-integrity",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var fixture = await CreateFixtureAsync(root);
            var builder = new MinecraftLaunchPlanBuilder(
                fixture.Paths);

            _ = await builder.BuildAsync(
                fixture.Instance,
                fixture.Account,
                [fixture.Java], fixture.Credentials);

            var extracted = Path.Combine(
                fixture.NativesRoot,
                "nested",
                "native.bin");
            var correct = await File.ReadAllBytesAsync(
                extracted);

            var corrupt = correct.ToArray();
            corrupt[corrupt.Length / 2] ^= 0x5A;
            await File.WriteAllBytesAsync(
                extracted,
                corrupt);
            await ExpectLaunchRejectedAsync(
                builder,
                fixture,
                "same-size corrupted extracted native");

            MinecraftVanillaInstallService.ExtractNativeArchive(
                fixture.NativeArchive,
                fixture.NativesRoot,
                ["META-INF/"]);
            File.Delete(extracted);
            await ExpectLaunchRejectedAsync(
                builder,
                fixture,
                "missing extracted native");

            MinecraftVanillaInstallService.ExtractNativeArchive(
                fixture.NativeArchive,
                fixture.NativesRoot,
                ["META-INF/"]);
            var stale = Path.Combine(
                fixture.NativesRoot,
                "stale-extra.bin");
            await File.WriteAllBytesAsync(
                stale,
                [1, 2, 3]);
            await ExpectLaunchRejectedAsync(
                builder,
                fixture,
                "unexpected stale extracted native");

            MinecraftVanillaInstallService.ExtractNativeArchive(
                fixture.NativeArchive,
                fixture.NativesRoot,
                ["META-INF/"]);
            _ = await builder.BuildAsync(
                fixture.Instance,
                fixture.Account,
                [fixture.Java], fixture.Credentials);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static async Task ExpectLaunchRejectedAsync(
        MinecraftLaunchPlanBuilder builder,
        Fixture fixture,
        string label)
    {
        try
        {
            _ = await builder.BuildAsync(
                fixture.Instance,
                fixture.Account,
                [fixture.Java], fixture.Credentials);
            throw new Exception(
                label + " unexpectedly remained launchable.");
        }
        catch (FileNotFoundException ex)
        {
            Assert(
                ex.Message.Contains(
                    "Prepare instance files again",
                    StringComparison.OrdinalIgnoreCase),
                label + " should direct the user to Prepare.");
        }
    }

    private static async Task<Fixture> CreateFixtureAsync(
        string root)
    {
        const string versionId = "native-integrity";
        const string assetId = "native-assets";
        const string nativeRelative =
            "com/example/native/1.0/native-1.0-natives.jar";

        var paths = new NexoPathService(root);
        paths.EnsureDirectories();
        var instance = new GameInstance(
            Guid.NewGuid().ToString("N"),
            "Native integrity",
            versionId,
            "vanilla",
            DateTimeOffset.UtcNow);
        var gameRoot = paths.GetInstanceGameDirectory(
            instance.Id);
        var versionRoot = Path.Combine(
            gameRoot,
            "versions",
            versionId);
        var librariesRoot = Path.Combine(
            gameRoot,
            "libraries");
        var assetsRoot = Path.Combine(
            gameRoot,
            "assets");
        var nativesRoot = Path.Combine(
            gameRoot,
            "natives",
            versionId);
        Directory.CreateDirectory(versionRoot);
        Directory.CreateDirectory(
            Path.Combine(assetsRoot, "indexes"));

        var clientBytes = Encoding.UTF8.GetBytes(
            "native-integrity-client");
        await File.WriteAllBytesAsync(
            Path.Combine(
                versionRoot,
                versionId + ".jar"),
            clientBytes);

        var indexBytes = Encoding.UTF8.GetBytes(
            "{\"objects\":{}}");
        await File.WriteAllBytesAsync(
            Path.Combine(
                assetsRoot,
                "indexes",
                assetId + ".json"),
            indexBytes);

        var nativeArchive = Path.Combine(
            librariesRoot,
            nativeRelative.Replace(
                '/',
                Path.DirectorySeparatorChar));
        Directory.CreateDirectory(
            Path.GetDirectoryName(nativeArchive)!);
        CreateNativeZip(
            nativeArchive,
            ("nested/native.bin", "native-payload"));

        MinecraftVanillaInstallService.ExtractNativeArchive(
            nativeArchive,
            nativesRoot,
            ["META-INF/"]);

        var classifier =
            "natives-" + MinecraftRules.OsName;
        var nativeBytes =
            await File.ReadAllBytesAsync(nativeArchive);

        var library = new Dictionary<string, object?>
        {
            ["natives"] = new Dictionary<string, string>
            {
                [MinecraftRules.OsName] = classifier
            },
            ["downloads"] = new
            {
                classifiers = new Dictionary<string, object>
                {
                    [classifier] = new
                    {
                        path = nativeRelative,
                        size = nativeBytes.LongLength,
                        sha1 = Sha1(nativeBytes)
                    }
                }
            },
            ["extract"] = new
            {
                exclude = new[]
                {
                    "META-INF/"
                }
            }
        };

        var metadata = JsonSerializer.Serialize(new
        {
            id = versionId,
            type = "release",
            mainClass =
                "net.minecraft.client.main.Main",
            javaVersion = new
            {
                majorVersion = 21
            },
            downloads = new
            {
                client = new
                {
                    size = clientBytes.LongLength,
                    sha1 = Sha1(clientBytes)
                }
            },
            assetIndex = new
            {
                id = assetId,
                size = indexBytes.LongLength,
                sha1 = Sha1(indexBytes)
            },
            libraries = new object[]
            {
                library
            },
            arguments = new
            {
                jvm = new[]
                {
                    "-Djava.library.path=${natives_directory}",
                    "-cp",
                    "${classpath}"
                },
                game = Array.Empty<string>()
            }
        });
        await File.WriteAllTextAsync(
            Path.Combine(
                versionRoot,
                versionId + ".json"),
            metadata);

        var javaPath = Path.Combine(
            root,
            OperatingSystem.IsWindows()
                ? "java.exe"
                : "java");
        await File.WriteAllBytesAsync(
            javaPath,
            [1]);
        var java = new JavaInstallation(
            javaPath,
            root,
            "21.0.8",
            true,
            "native integrity");
        var identity = TestMicrosoftIdentity.Create("NativeUser");
        var account = identity.Account;

        return new Fixture(
            paths,
            instance,
            account,
            identity.Credentials,
            java,
            nativesRoot,
            nativeArchive);
    }

    private static void CreateNativeZip(
        string path,
        params (string Name, string Content)[] entries)
    {
        using var file = File.Create(path);
        using var archive = new ZipArchive(
            file,
            ZipArchiveMode.Create);
        foreach (var item in entries)
        {
            var entry = archive.CreateEntry(item.Name);
            using var output = entry.Open();
            var bytes = Encoding.UTF8.GetBytes(
                item.Content);
            output.Write(bytes);
        }
    }

    private static string Sha1(byte[] value)
        => Convert.ToHexString(
                SHA1.HashData(value))
            .ToLowerInvariant();

    private static void Assert(
        bool condition,
        string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed record Fixture(
        NexoPathService Paths,
        GameInstance Instance,
        LauncherAccount Account,
        MinecraftLaunchCredentials Credentials,
        JavaInstallation Java,
        string NativesRoot,
        string NativeArchive);
}
