using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UN.Nexo.Core.Launching;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class LaunchIntegrityRegression
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-launch-integrity-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var fixture = await CreateFixtureAsync(root);
            var builder = new MinecraftLaunchPlanBuilder(fixture.Paths);

            _ = await builder.BuildAsync(
                fixture.Instance,
                fixture.Account,
                [fixture.Java]);

            await AssertSameSizeCorruptionRejectedAsync(
                fixture.ClientPath,
                fixture.ClientBytes,
                () => builder.BuildAsync(
                    fixture.Instance,
                    fixture.Account,
                    [fixture.Java]),
                "same-size corrupted client JAR");

            await AssertSameSizeCorruptionRejectedAsync(
                fixture.LibraryPath,
                fixture.LibraryBytes,
                () => builder.BuildAsync(
                    fixture.Instance,
                    fixture.Account,
                    [fixture.Java]),
                "same-size corrupted library JAR");

            await AssertSameSizeCorruptionRejectedAsync(
                fixture.ObjectPath,
                fixture.ObjectBytes,
                () => builder.BuildAsync(
                    fixture.Instance,
                    fixture.Account,
                    [fixture.Java]),
                "same-size corrupted authoritative asset object");

            _ = await builder.BuildAsync(
                fixture.Instance,
                fixture.Account,
                [fixture.Java]);

            var virtualPath = Path.Combine(
                fixture.GameRoot,
                "assets",
                "virtual",
                fixture.AssetId,
                "minecraft",
                "test",
                "payload.bin");
            var resourcePath = Path.Combine(
                fixture.GameRoot,
                "resources",
                "minecraft",
                "test",
                "payload.bin");

            Assert(
                File.Exists(virtualPath),
                "Initial launch plan should materialize the virtual asset.");
            Assert(
                File.Exists(resourcePath),
                "Initial launch plan should materialize the resources asset.");

            var corruptDerived = CorruptSameLength(
                fixture.ObjectBytes);
            await File.WriteAllBytesAsync(
                virtualPath,
                corruptDerived);
            await File.WriteAllBytesAsync(
                resourcePath,
                corruptDerived);

            _ = await builder.BuildAsync(
                fixture.Instance,
                fixture.Account,
                [fixture.Java]);

            Assert(
                fixture.ObjectBytes.AsSpan().SequenceEqual(
                    await File.ReadAllBytesAsync(virtualPath)),
                "Same-size corrupted virtual asset must be restored from the authoritative object.");
            Assert(
                fixture.ObjectBytes.AsSpan().SequenceEqual(
                    await File.ReadAllBytesAsync(resourcePath)),
                "Same-size corrupted resources asset must be restored from the authoritative object.");

            _ = await builder.BuildAsync(
                fixture.Instance,
                fixture.Account,
                [fixture.Java]);

            Assert(
                fixture.ObjectBytes.AsSpan().SequenceEqual(
                    await File.ReadAllBytesAsync(virtualPath)),
                "Already-correct virtual asset should remain correct.");
            Assert(
                fixture.ObjectBytes.AsSpan().SequenceEqual(
                    await File.ReadAllBytesAsync(resourcePath)),
                "Already-correct resources asset should remain correct.");
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

    private static async Task AssertSameSizeCorruptionRejectedAsync(
        string path,
        byte[] correctBytes,
        Func<Task<MinecraftLaunchPlan>> build,
        string label)
    {
        var corrupt = CorruptSameLength(correctBytes);
        await File.WriteAllBytesAsync(path, corrupt);
        Assert(
            new FileInfo(path).Length == correctBytes.LongLength,
            label + " fixture must preserve file length.");

        try
        {
            _ = await build();
            throw new Exception(
                label + " unexpectedly passed launch validation.");
        }
        catch (FileNotFoundException ex)
        {
            Assert(
                ex.Message.Contains(
                    "Prepare instance files again",
                    StringComparison.OrdinalIgnoreCase),
                label + " should produce the actionable Prepare/Repair path.");
        }
        finally
        {
            await File.WriteAllBytesAsync(
                path,
                correctBytes);
        }
    }

    private static byte[] CorruptSameLength(
        byte[] bytes)
    {
        if (bytes.Length == 0)
            throw new ArgumentException(
                "Integrity fixture bytes must be non-empty.",
                nameof(bytes));

        var result = bytes.ToArray();
        result[result.Length / 2] ^= 0x5A;
        return result;
    }

    private static async Task<Fixture> CreateFixtureAsync(
        string root)
    {
        const string versionId = "integrity-fixture";
        const string assetId = "integrity-assets";

        var paths = new NexoPathService(root);
        var instance = new GameInstance(
            Guid.NewGuid().ToString("N"),
            "Launch integrity fixture",
            versionId,
            "vanilla",
            DateTimeOffset.UtcNow);
        var account = new LauncherAccount(
            "offline:integrity",
            "offline",
            "IntegrityUser",
            Guid.NewGuid().ToString("D"),
            DateTimeOffset.UtcNow);

        var gameRoot = paths.GetInstanceGameDirectory(
            instance.Id);
        var instanceRoot = paths.GetInstanceDirectory(
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
        Directory.CreateDirectory(versionRoot);
        Directory.CreateDirectory(librariesRoot);
        Directory.CreateDirectory(
            Path.Combine(assetsRoot, "indexes"));
        Directory.CreateDirectory(instanceRoot);

        var clientBytes = Encoding.UTF8.GetBytes(
            "verified-client-jar-payload");
        var clientPath = Path.Combine(
            versionRoot,
            versionId + ".jar");
        await File.WriteAllBytesAsync(
            clientPath,
            clientBytes);

        const string libraryRelative =
            "com/example/integrity/1.0/integrity-1.0.jar";
        var libraryPath = Path.Combine(
            librariesRoot,
            libraryRelative.Replace(
                '/',
                Path.DirectorySeparatorChar));
        Directory.CreateDirectory(
            Path.GetDirectoryName(libraryPath)!);
        var libraryBytes = Encoding.UTF8.GetBytes(
            "verified-library-payload");
        await File.WriteAllBytesAsync(
            libraryPath,
            libraryBytes);

        var objectBytes = Encoding.UTF8.GetBytes(
            "verified-asset-object-payload");
        var objectSha1 = Sha1(objectBytes);
        var objectPath = Path.Combine(
            assetsRoot,
            "objects",
            objectSha1[..2],
            objectSha1);
        Directory.CreateDirectory(
            Path.GetDirectoryName(objectPath)!);
        await File.WriteAllBytesAsync(
            objectPath,
            objectBytes);

        var index = JsonSerializer.Serialize(new
        {
            @virtual = true,
            map_to_resources = true,
            objects = new Dictionary<string, object>
            {
                ["minecraft/test/payload.bin"] = new
                {
                    hash = objectSha1,
                    size = objectBytes.LongLength
                }
            }
        });
        var indexBytes = Encoding.UTF8.GetBytes(index);
        var indexPath = Path.Combine(
            assetsRoot,
            "indexes",
            assetId + ".json");
        await File.WriteAllBytesAsync(
            indexPath,
            indexBytes);

        var metadata = JsonSerializer.Serialize(new
        {
            id = versionId,
            type = "release",
            mainClass = "net.minecraft.client.main.Main",
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
                size = indexBytes.LongLength
            },
            libraries = new[]
            {
                new
                {
                    name = "com.example:integrity:1.0",
                    downloads = new
                    {
                        artifact = new
                        {
                            path = libraryRelative,
                            size = libraryBytes.LongLength,
                            sha1 = Sha1(libraryBytes)
                        }
                    }
                }
            },
            arguments = new
            {
                jvm = new[]
                {
                    "-cp",
                    "${classpath}"
                },
                game = new[]
                {
                    "--username",
                    "${auth_player_name}"
                }
            }
        });
        await File.WriteAllTextAsync(
            Path.Combine(
                versionRoot,
                versionId + ".json"),
            metadata);

        await File.WriteAllTextAsync(
            Path.Combine(
                instanceRoot,
                "install-state.json"),
            "{}");

        var javaPath = Path.Combine(
            root,
            OperatingSystem.IsWindows()
                ? "java.exe"
                : "java");
        await File.WriteAllBytesAsync(
            javaPath,
            [0x01]);
        var java = new JavaInstallation(
            javaPath,
            root,
            "21.0.8",
            true,
            "integrity fixture");

        return new Fixture(
            paths,
            instance,
            account,
            java,
            gameRoot,
            assetId,
            clientPath,
            clientBytes,
            libraryPath,
            libraryBytes,
            objectPath,
            objectBytes);
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
        JavaInstallation Java,
        string GameRoot,
        string AssetId,
        string ClientPath,
        byte[] ClientBytes,
        string LibraryPath,
        byte[] LibraryBytes,
        string ObjectPath,
        byte[] ObjectBytes);
}
