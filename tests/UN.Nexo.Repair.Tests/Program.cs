using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Repair.Tests;

internal static class Program
{
    private static async Task<int> Main()
    {
        var temp = Path.Combine(Path.GetTempPath(), "nexo-repair-regression", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new NexoPathService(temp);
            paths.EnsureDirectories();
            var instance = new GameInstance("repair-test", "Repair test", "repair-test", "vanilla", DateTimeOffset.UtcNow);
            var gameRoot = paths.GetInstanceGameDirectory(instance.Id);
            var versionRoot = Path.Combine(gameRoot, "versions", instance.VersionId);
            var librariesRoot = Path.Combine(gameRoot, "libraries");
            var assetsRoot = Path.Combine(gameRoot, "assets");
            Directory.CreateDirectory(versionRoot);
            Directory.CreateDirectory(librariesRoot);
            Directory.CreateDirectory(Path.Combine(assetsRoot, "indexes"));
            Directory.CreateDirectory(Path.Combine(assetsRoot, "objects"));
            Directory.CreateDirectory(Path.Combine(paths.GetInstanceDirectory(instance.Id), "game", "saves", "World"));

            var savePath = Path.Combine(gameRoot, "saves", "World", "level.dat");
            await File.WriteAllTextAsync(savePath, "keep-me");
            await File.WriteAllTextAsync(Path.Combine(paths.GetInstanceDirectory(instance.Id), "install-state.json"), "{}");

            var expectedClient = Encoding.UTF8.GetBytes("expected-client");
            var clientSha = Sha1(expectedClient);
            await File.WriteAllTextAsync(Path.Combine(versionRoot, "repair-test.jar"), "broken-client");

            var expectedLibrary = Encoding.UTF8.GetBytes("expected-library");
            var librarySha = Sha1(expectedLibrary);
            var libraryRelative = "example/library/1.0/library-1.0.jar";

            var osKey = OperatingSystem.IsWindows() ? "windows" : "linux";
            var classifier = $"natives-{osKey}";
            var nativeRelative = $"example/native/1.0/native-1.0-{classifier}.jar";
            var nativeArchivePath = Path.Combine(librariesRoot, nativeRelative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(nativeArchivePath)!);
            await CreateNativeArchiveAsync(nativeArchivePath, OperatingSystem.IsWindows() ? "native-test.dll" : "libnative-test.so");
            var nativeSha = await Sha1FileAsync(nativeArchivePath);

            var expectedAsset = Encoding.UTF8.GetBytes("expected-asset");
            var assetSha = Sha1(expectedAsset);
            var assetIndex = new JsonObject
            {
                ["objects"] = new JsonObject
                {
                    ["test/resource.txt"] = new JsonObject
                    {
                        ["hash"] = assetSha,
                        ["size"] = expectedAsset.Length
                    }
                }
            };
            var assetIndexPath = Path.Combine(assetsRoot, "indexes", "repair-assets.json");
            await File.WriteAllTextAsync(assetIndexPath, assetIndex.ToJsonString());
            var assetIndexSha = await Sha1FileAsync(assetIndexPath);

            var version = new JsonObject
            {
                ["id"] = instance.VersionId,
                ["type"] = "release",
                ["javaVersion"] = new JsonObject { ["majorVersion"] = 999 },
                ["downloads"] = new JsonObject
                {
                    ["client"] = new JsonObject
                    {
                        ["url"] = "https://example.invalid/client.jar",
                        ["sha1"] = clientSha
                    }
                },
                ["assetIndex"] = new JsonObject
                {
                    ["id"] = "repair-assets",
                    ["url"] = "https://example.invalid/assets.json",
                    ["sha1"] = assetIndexSha
                }
            };
            var libraries = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "example:library:1.0",
                    ["downloads"] = new JsonObject
                    {
                        ["artifact"] = new JsonObject
                        {
                            ["path"] = libraryRelative,
                            ["url"] = "https://example.invalid/library.jar",
                            ["sha1"] = librarySha
                        }
                    }
                },
                new JsonObject
                {
                    ["name"] = "example:native:1.0",
                    ["downloads"] = new JsonObject
                    {
                        ["classifiers"] = new JsonObject
                        {
                            [classifier] = new JsonObject
                            {
                                ["path"] = nativeRelative,
                                ["url"] = "https://example.invalid/native.jar",
                                ["sha1"] = nativeSha
                            }
                        }
                    },
                    ["natives"] = new JsonObject { [osKey] = classifier }
                }
            };
            version["libraries"] = libraries;
            await File.WriteAllTextAsync(Path.Combine(versionRoot, "repair-test.json"), version.ToJsonString());

            var sources = new DownloadSourceService();
            using var client = new HttpClient(new FailNetworkHandler());
            var service = new MinecraftInstanceRepairService(
                paths,
                new MinecraftVanillaInstallService(client, paths, sources),
                new MinecraftVersionManifestService(client, sources),
                new JavaDiscoveryService(paths),
                new MinecraftRuntimeInspector(paths),
                new JavaRuntimeProvisionService(client, paths));

            var report = await service.CheckAsync(instance);
            Require(report.Issues.Any(item => item.Code == "client-corrupt"), "corrupt client should be detected");
            Require(report.Issues.Any(item => item.Code == "libraries-damaged"), "missing library should be detected");
            Require(report.Issues.Any(item => item.Code == "natives-damaged"), "missing extracted native should be detected");
            Require(report.Issues.Any(item => item.Code == "assets-damaged"), "missing asset should be detected");
            Require(report.Issues.Any(item => item.Code == "java-missing"), "wrong Java major should be detected");
            Require(await File.ReadAllTextAsync(savePath) == "keep-me", "integrity scan must not modify world data");

            var versionPath = Path.Combine(versionRoot, "repair-test.json");
            var validVersionJson = version.ToJsonString();
            var malformedVersions = new[]
            {
                "[]",
                "{\"downloads\":[]}",
                "{\"downloads\":{\"client\":123}}",
                "{\"libraries\":[123]}",
                "{\"assetIndex\":123}",
                "{\"libraries\":[{\"downloads\":[]}]}",
                "{\"libraries\":[{\"downloads\":{},\"natives\":{\"" + osKey + "\":123}}]}",
                "{\"libraries\":[{\"downloads\":{},\"extract\":{\"exclude\":{}}}]}"
            };
            foreach (var malformedVersion in malformedVersions)
            {
                await File.WriteAllTextAsync(versionPath, malformedVersion);
                var malformedReport = await service.CheckAsync(instance);
                Require(
                    malformedReport.Issues.Any(item => item.Code == "version-metadata-invalid"),
                    $"wrong-shaped version metadata should produce version-metadata-invalid: {malformedVersion}");
            }
            await File.WriteAllTextAsync(versionPath, validVersionJson);

            var validAssetIndexJson = assetIndex.ToJsonString();
            var malformedAssetIndexes = new[]
            {
                "{}",
                "{\"objects\":null}",
                "{\"objects\":[]}",
                "{\"objects\":{\"x\":123}}",
                "{\"objects\":{\"x\":{\"hash\":null}}}",
                "{\"objects\":{\"x\":{\"hash\":123}}}",
                "{\"objects\":{\"x\":{\"hash\":\"a\"}}}"
            };
            foreach (var malformedIndex in malformedAssetIndexes)
            {
                await File.WriteAllTextAsync(assetIndexPath, malformedIndex);
                ((JsonObject)version["assetIndex"]!)["sha1"] = await Sha1FileAsync(assetIndexPath);
                await File.WriteAllTextAsync(versionPath, version.ToJsonString());

                var malformedReport = await service.CheckAsync(instance);
                Require(
                    malformedReport.Issues.Any(item => item.Code == "asset-index-invalid"),
                    $"wrong-shaped asset index should produce asset-index-invalid: {malformedIndex}");
            }

            await File.WriteAllTextAsync(assetIndexPath, validAssetIndexJson);
            ((JsonObject)version["assetIndex"]!)["sha1"] = await Sha1FileAsync(assetIndexPath);
            await File.WriteAllTextAsync(versionPath, version.ToJsonString());

            var unsafeIds = new[] { "../outside", "a/b", @"a\b", ".", "..", "/rooted", @"C:\outside" };
            foreach (var unsafeId in unsafeIds)
            {
                ((JsonObject)version["assetIndex"]!)["id"] = unsafeId;
                await File.WriteAllTextAsync(
                    Path.Combine(versionRoot, "repair-test.json"),
                    version.ToJsonString());

                var unsafeReport = await service.CheckAsync(instance);
                Require(
                    unsafeReport.Issues.Any(item => item.Code == "asset-index-invalid-metadata"),
                    $"unsafe assetIndex.id '{unsafeId}' should be reported as invalid metadata");
                Require(
                    unsafeReport.Issues.All(item =>
                        item.Code is not "asset-index-missing" and not "asset-index-corrupt"),
                    $"unsafe assetIndex.id '{unsafeId}' must not be inspected as an asset-index file");
            }

            var escapedIndexPath = Path.Combine(assetsRoot, "outside.json");
            await File.WriteAllTextAsync(
                escapedIndexPath,
                new JsonObject
                {
                    ["virtual"] = true,
                    ["objects"] = new JsonObject
                    {
                        ["escape.txt"] = new JsonObject
                        {
                            ["hash"] = assetSha,
                            ["size"] = expectedAsset.Length
                        }
                    }
                }.ToJsonString());
            var objectPath = Path.Combine(assetsRoot, "objects", assetSha[..2], assetSha);
            Directory.CreateDirectory(Path.GetDirectoryName(objectPath)!);
            await File.WriteAllBytesAsync(objectPath, expectedAsset);

            ((JsonObject)version["assetIndex"]!)["id"] = "../outside";
            await File.WriteAllTextAsync(
                Path.Combine(versionRoot, "repair-test.json"),
                version.ToJsonString());

            var rebuildMethod = typeof(MinecraftInstanceRepairService).GetMethod(
                "RebuildMappedAssetsAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("RebuildMappedAssetsAsync regression hook not found.");
            var rebuildTask = (Task?)rebuildMethod.Invoke(
                service,
                [instance, CancellationToken.None])
                ?? throw new InvalidOperationException("RebuildMappedAssetsAsync did not return a task.");
            try
            {
                await rebuildTask;
                throw new InvalidOperationException(
                    "Unsafe assetIndex.id should be rejected by the mapped-assets rebuild boundary.");
            }
            catch (InvalidDataException ex)
            {
                Require(
                    ex.Message.Contains("assetIndex.id", StringComparison.Ordinal),
                    "mapped-assets rejection should identify assetIndex.id");
            }

            Require(
                !File.Exists(Path.Combine(assetsRoot, "outside", "escape.txt")),
                "mapped-assets rebuild must not write through an escaped virtual root");

            ((JsonObject)version["assetIndex"]!)["id"] = "repair-assets";
            await File.WriteAllTextAsync(
                Path.Combine(versionRoot, "repair-test.json"),
                version.ToJsonString());

            Console.WriteLine($"PASS repair regression: {report.ErrorCount} errors, {report.WarningCount} warnings detected without touching saves");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL repair regression: {ex}");
            return 1;
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private static async Task CreateNativeArchiveAsync(string path, string entryName)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 4096, useAsync: true);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        var entry = archive.CreateEntry(entryName);
        await using var entryStream = entry.Open();
        await entryStream.WriteAsync(Encoding.UTF8.GetBytes("native-payload"));
    }

    private static string Sha1(byte[] value)
    {
        using var sha = SHA1.Create();
        return Convert.ToHexString(sha.ComputeHash(value)).ToLowerInvariant();
    }

    private static async Task<string> Sha1FileAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA1.Create();
        var hash = await sha.ComputeHashAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class FailNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
