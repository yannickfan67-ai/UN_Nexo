using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UN.Nexo.Core.Launching;
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
            var instance = new GameInstance(Guid.NewGuid().ToString("N"), "Repair test", "repair-test", "vanilla", DateTimeOffset.UtcNow);
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

            await RunFabricRepairInheritanceRegressionAsync();

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

    private static async Task RunFabricRepairInheritanceRegressionAsync()
    {
        var temp = Path.Combine(
            Path.GetTempPath(),
            "nexo-fabric-repair-regression",
            Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new NexoPathService(temp);
            paths.EnsureDirectories();
            var javaDiscovery = new JavaDiscoveryService(paths);
            var installedJava = (await javaDiscovery.DiscoverAsync())
                .FirstOrDefault(item =>
                    item.Is64Bit
                    && MinecraftLaunchPlanBuilder.JavaMajor(item.Version) > 0);
            Require(installedJava is not null,
                "Fabric repair regression requires one discoverable 64-bit Java runtime.");
            var javaMajor = MinecraftLaunchPlanBuilder.JavaMajor(installedJava!.Version);

            const string baseVersionId = "1.21.4";
            const string loaderVersion = "0.16.9";
            const string childVersionId = "fabric-loader-0.16.9-1.21.4";
            var instance = new GameInstance(
                Guid.NewGuid().ToString("N"),
                "Fabric repair test",
                childVersionId,
                "fabric",
                DateTimeOffset.UtcNow,
                baseVersionId,
                loaderVersion);

            var gameRoot = paths.GetInstanceGameDirectory(instance.Id);
            var baseRoot = Path.Combine(gameRoot, "versions", baseVersionId);
            var childRoot = Path.Combine(gameRoot, "versions", childVersionId);
            var librariesRoot = Path.Combine(gameRoot, "libraries");
            var assetsRoot = Path.Combine(gameRoot, "assets");
            Directory.CreateDirectory(baseRoot);
            Directory.CreateDirectory(childRoot);
            Directory.CreateDirectory(librariesRoot);
            Directory.CreateDirectory(Path.Combine(assetsRoot, "indexes"));
            Directory.CreateDirectory(Path.Combine(assetsRoot, "objects"));
            await File.WriteAllTextAsync(
                Path.Combine(paths.GetInstanceDirectory(instance.Id), "install-state.json"),
                "{}");

            var clientBytes = Encoding.UTF8.GetBytes("fabric-parent-client");
            var clientSha = Sha1(clientBytes);
            var baseLibraryBytes = Encoding.UTF8.GetBytes("fabric-parent-library");
            var baseLibrarySha = Sha1(baseLibraryBytes);
            const string baseLibraryRelative = "example/base/1.0/base-1.0.jar";
            var fabricLibraryBytes = Encoding.UTF8.GetBytes("fabric-loader-library");
            var fabricLibrarySha = Sha1(fabricLibraryBytes);
            const string fabricLibraryRelative =
                "net/fabricmc/fabric-loader/0.16.9/fabric-loader-0.16.9.jar";
            var assetBytes = Encoding.UTF8.GetBytes("fabric-parent-asset");
            var assetSha = Sha1(assetBytes);

            var assetIndex = new JsonObject
            {
                ["objects"] = new JsonObject
                {
                    ["minecraft/fabric-test.txt"] = new JsonObject
                    {
                        ["hash"] = assetSha,
                        ["size"] = assetBytes.Length
                    }
                }
            };
            var assetIndexPath = Path.Combine(assetsRoot, "indexes", "fabric-assets.json");
            await File.WriteAllTextAsync(assetIndexPath, assetIndex.ToJsonString());
            var assetIndexSha = await Sha1FileAsync(assetIndexPath);

            var baseMetadata = new JsonObject
            {
                ["id"] = baseVersionId,
                ["type"] = "release",
                ["javaVersion"] = new JsonObject { ["majorVersion"] = javaMajor },
                ["downloads"] = new JsonObject
                {
                    ["client"] = new JsonObject
                    {
                        ["url"] = "https://piston-data.mojang.com/client.jar",
                        ["sha1"] = clientSha
                    }
                },
                ["assetIndex"] = new JsonObject
                {
                    ["id"] = "fabric-assets",
                    ["url"] = "https://piston-data.mojang.com/assets.json",
                    ["sha1"] = assetIndexSha
                },
                ["libraries"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["name"] = "example:base:1.0",
                        ["downloads"] = new JsonObject
                        {
                            ["artifact"] = new JsonObject
                            {
                                ["path"] = baseLibraryRelative,
                                ["url"] = "https://piston-data.mojang.com/base-library.jar",
                                ["sha1"] = baseLibrarySha
                            }
                        }
                    }
                }
            };
            var baseMetadataPath = Path.Combine(baseRoot, baseVersionId + ".json");
            await File.WriteAllTextAsync(baseMetadataPath, baseMetadata.ToJsonString());

            var childMetadata = new JsonObject
            {
                ["id"] = childVersionId,
                ["inheritsFrom"] = baseVersionId,
                ["libraries"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["name"] = "net.fabricmc:fabric-loader:" + loaderVersion,
                        ["downloads"] = new JsonObject
                        {
                            ["artifact"] = new JsonObject
                            {
                                ["path"] = fabricLibraryRelative,
                                ["url"] = "https://piston-data.mojang.com/fabric-loader.jar",
                                ["sha1"] = fabricLibrarySha
                            }
                        }
                    }
                }
            };
            var childMetadataPath = Path.Combine(childRoot, childVersionId + ".json");
            await File.WriteAllTextAsync(childMetadataPath, childMetadata.ToJsonString());

            var clientPath = Path.Combine(baseRoot, baseVersionId + ".jar");
            await File.WriteAllBytesAsync(clientPath, clientBytes);
            var baseLibraryPath = Path.Combine(
                librariesRoot,
                baseLibraryRelative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(baseLibraryPath)!);
            await File.WriteAllBytesAsync(baseLibraryPath, baseLibraryBytes);
            var fabricLibraryPath = Path.Combine(
                librariesRoot,
                fabricLibraryRelative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fabricLibraryPath)!);
            await File.WriteAllBytesAsync(fabricLibraryPath, fabricLibraryBytes);
            var assetObjectPath = Path.Combine(
                assetsRoot,
                "objects",
                assetSha[..2],
                assetSha);
            Directory.CreateDirectory(Path.GetDirectoryName(assetObjectPath)!);
            await File.WriteAllBytesAsync(assetObjectPath, assetBytes);

            var handler = new FabricRepairHandler(
                baseVersionId,
                javaMajor,
                clientBytes,
                baseLibraryBytes,
                fabricLibraryBytes,
                assetBytes);
            using var client = new HttpClient(handler);
            var sources = new DownloadSourceService();
            var vanillaInstaller = new MinecraftVanillaInstallService(client, paths, sources);
            var fabricInstaller = new FabricInstallService(
                client,
                paths,
                vanillaInstaller,
                new FabricMetaService(client));
            var service = new MinecraftInstanceRepairService(
                paths,
                vanillaInstaller,
                new MinecraftVersionManifestService(client, sources),
                javaDiscovery,
                new MinecraftRuntimeInspector(paths),
                new JavaRuntimeProvisionService(client, paths),
                fabricInstaller);

            var healthy = await service.CheckAsync(instance);
            Require(
                healthy.Issues.All(item =>
                    item.Code is not "client-missing"
                    and not "client-corrupt"
                    and not "libraries-damaged"
                    and not "assets-damaged"
                    and not "version-metadata-inheritance-invalid"),
                "Healthy Fabric inheritance fixture should not report inherited artifact damage.");

            File.Delete(clientPath);
            var missingClient = await service.CheckAsync(instance);
            Require(
                missingClient.Issues.Any(item => item.Code == "client-missing"),
                "Fabric health check should detect a missing inherited Vanilla client.");
            await File.WriteAllBytesAsync(clientPath, clientBytes);

            File.Delete(baseLibraryPath);
            var missingLibrary = await service.CheckAsync(instance);
            Require(
                missingLibrary.Issues.Any(item => item.Code == "libraries-damaged"),
                "Fabric health check should detect a missing inherited Vanilla library.");
            Directory.CreateDirectory(Path.GetDirectoryName(baseLibraryPath)!);
            await File.WriteAllBytesAsync(baseLibraryPath, baseLibraryBytes);

            File.Delete(assetObjectPath);
            var missingAsset = await service.CheckAsync(instance);
            Require(
                missingAsset.Issues.Any(item => item.Code == "assets-damaged"),
                "Fabric health check should detect a missing inherited asset object.");
            Directory.CreateDirectory(Path.GetDirectoryName(assetObjectPath)!);
            await File.WriteAllBytesAsync(assetObjectPath, assetBytes);

            File.Delete(baseMetadataPath);
            var missingParent = await service.CheckAsync(instance);
            Require(
                missingParent.Issues.Any(item => item.Code == "version-metadata-inheritance-invalid"),
                "Missing Fabric parent metadata should be an actionable inheritance health issue.");
            await File.WriteAllTextAsync(baseMetadataPath, baseMetadata.ToJsonString());

            File.Delete(clientPath);
            File.Delete(baseLibraryPath);
            File.Delete(fabricLibraryPath);
            File.Delete(assetObjectPath);
            File.Delete(Path.Combine(paths.GetInstanceDirectory(instance.Id), "install-state.json"));

            var repaired = await service.RepairAsync(instance);
            Require(repaired.ErrorCount == 0,
                "Fabric Repair should complete with no remaining file-health errors.");
            Require(File.Exists(clientPath),
                "Fabric Repair should restore the inherited Vanilla client.");
            Require(File.Exists(baseLibraryPath),
                "Fabric Repair should restore inherited Vanilla libraries.");
            Require(File.Exists(fabricLibraryPath),
                "Fabric Repair should restore Fabric loader libraries.");
            Require(File.Exists(assetObjectPath),
                "Fabric Repair should restore inherited assets.");
            Require(File.Exists(Path.Combine(paths.GetInstanceDirectory(instance.Id), "install-state.json")),
                "Fabric Repair should publish prepared install state.");
            Require(handler.ManifestRequests > 0,
                "Fabric Repair should resolve the base Minecraft version through the Mojang catalog.");
            Require(handler.FabricLibraryRequests > 0,
                "Fabric Repair should route through Fabric preparation instead of Vanilla profile lookup.");
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

    private sealed class FabricRepairHandler(
        string baseVersionId,
        int javaMajor,
        byte[] clientBytes,
        byte[] baseLibraryBytes,
        byte[] fabricLibraryBytes,
        byte[] assetBytes) : HttpMessageHandler
    {
        public int ManifestRequests { get; private set; }
        public int FabricLibraryRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Missing request URI.");
            if (uri.AbsolutePath.Contains("version_manifest", StringComparison.OrdinalIgnoreCase))
            {
                ManifestRequests++;
                var manifest =
                    "{\"latest\":{\"release\":\"" + baseVersionId + "\",\"snapshot\":\"" + baseVersionId + "\"},"
                    + "\"versions\":[{\"id\":\"" + baseVersionId + "\",\"type\":\"release\","
                    + "\"url\":\"https://piston-data.mojang.com/version.json\","
                    + "\"releaseTime\":\"2026-01-01T00:00:00Z\","
                    + "\"time\":\"2026-01-01T00:00:00Z\",\"sha1\":\"\",\"complianceLevel\":1}]}";
                return Task.FromResult(Json(manifest));
            }

            if (uri.Host.Equals("piston-data.mojang.com", StringComparison.OrdinalIgnoreCase))
            {
                return uri.AbsolutePath switch
                {
                    "/version.json" => Task.FromResult(Json(
                        BaseMetadata())),
                    "/client.jar" => Task.FromResult(Bytes(clientBytes)),
                    "/base-library.jar" => Task.FromResult(Bytes(baseLibraryBytes)),
                    "/fabric-loader.jar" => Task.FromResult(FabricBytes()),
                    "/assets.json" => Task.FromResult(Json(
                        AssetIndex())),
                    _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
                };
            }

            if (uri.Host.Equals("resources.download.minecraft.net", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(Bytes(assetBytes));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private string BaseMetadata()
        {
            var clientSha = Convert.ToHexString(
                    SHA1.HashData(clientBytes))
                .ToLowerInvariant();
            var librarySha = Convert.ToHexString(
                    SHA1.HashData(baseLibraryBytes))
                .ToLowerInvariant();
            var indexBytes = Encoding.UTF8.GetBytes(
                AssetIndex());
            var indexSha = Convert.ToHexString(
                    SHA1.HashData(indexBytes))
                .ToLowerInvariant();

            return "{\"id\":\"" + baseVersionId + "\","
                   + "\"type\":\"release\","
                   + "\"javaVersion\":{\"majorVersion\":" + javaMajor + "},"
                   + "\"downloads\":{\"client\":{"
                   + "\"url\":\"https://piston-data.mojang.com/client.jar\","
                   + "\"sha1\":\"" + clientSha + "\"}},"
                   + "\"assetIndex\":{"
                   + "\"id\":\"fabric-assets\","
                   + "\"url\":\"https://piston-data.mojang.com/assets.json\","
                   + "\"sha1\":\"" + indexSha + "\"},"
                   + "\"libraries\":[{"
                   + "\"name\":\"example:base:1.0\","
                   + "\"downloads\":{\"artifact\":{"
                   + "\"path\":\"example/base/1.0/base-1.0.jar\","
                   + "\"url\":\"https://piston-data.mojang.com/base-library.jar\","
                   + "\"sha1\":\"" + librarySha + "\"}}}]}";
        }

        private string AssetIndex()
        {
            var assetSha = Convert.ToHexString(
                    SHA1.HashData(assetBytes))
                .ToLowerInvariant();
            return "{\"objects\":{"
                   + "\"minecraft/fabric-test.txt\":{"
                   + "\"hash\":\"" + assetSha + "\","
                   + "\"size\":" + assetBytes.LongLength
                   + "}}}";
        }

        private HttpResponseMessage FabricBytes()
        {
            FabricLibraryRequests++;
            return Bytes(fabricLibraryBytes);
        }

        private static HttpResponseMessage Json(string value)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(value, Encoding.UTF8, "application/json")
            };

        private static HttpResponseMessage Bytes(byte[] value)
            => new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(value)
            };
    }

    private sealed class FailNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
