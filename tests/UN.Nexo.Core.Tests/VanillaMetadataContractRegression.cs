using System.Net;
using System.Text;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class VanillaMetadataContractRegression
{
    internal static async Task RunAsync()
    {
        await TestInvalidMetadataFailsBeforeArtifactsAsync();
        await TestInvalidRefreshPreservesExistingStateAsync();
        await TestValidMetadataPreparesAndResolvesAsync();
    }

    private static async Task TestInvalidMetadataFailsBeforeArtifactsAsync()
    {
        var fixtures = new (string Label, string Json)[]
        {
            ("mismatched id", ValidMetadata("1.20.1")),
            ("missing id", """{"downloads":{"client":{"url":"https://piston-data.mojang.com/client.jar","size":6}},"assetIndex":{"id":"assets","url":"https://launchermeta.mojang.com/assets.json"},"libraries":[]}"""),
            ("numeric id", """{"id":123,"downloads":{"client":{"url":"https://piston-data.mojang.com/client.jar","size":6}},"assetIndex":{"id":"assets","url":"https://launchermeta.mojang.com/assets.json"},"libraries":[]}"""),
            ("missing downloads", """{"id":"1.21.4","assetIndex":{"id":"assets","url":"https://launchermeta.mojang.com/assets.json"},"libraries":[]}"""),
            ("array downloads", """{"id":"1.21.4","downloads":[],"assetIndex":{"id":"assets","url":"https://launchermeta.mojang.com/assets.json"},"libraries":[]}"""),
            ("missing client", """{"id":"1.21.4","downloads":{},"assetIndex":{"id":"assets","url":"https://launchermeta.mojang.com/assets.json"},"libraries":[]}"""),
            ("string client", """{"id":"1.21.4","downloads":{"client":"bad"},"assetIndex":{"id":"assets","url":"https://launchermeta.mojang.com/assets.json"},"libraries":[]}"""),
            ("missing client url", """{"id":"1.21.4","downloads":{"client":{}},"assetIndex":{"id":"assets","url":"https://launchermeta.mojang.com/assets.json"},"libraries":[]}"""),
            ("numeric client url", """{"id":"1.21.4","downloads":{"client":{"url":123}},"assetIndex":{"id":"assets","url":"https://launchermeta.mojang.com/assets.json"},"libraries":[]}"""),
            ("missing asset index", """{"id":"1.21.4","downloads":{"client":{"url":"https://piston-data.mojang.com/client.jar","size":6}},"libraries":[]}"""),
            ("array asset index", """{"id":"1.21.4","downloads":{"client":{"url":"https://piston-data.mojang.com/client.jar","size":6}},"assetIndex":[],"libraries":[]}"""),
            ("missing asset id", """{"id":"1.21.4","downloads":{"client":{"url":"https://piston-data.mojang.com/client.jar","size":6}},"assetIndex":{"url":"https://launchermeta.mojang.com/assets.json"},"libraries":[]}"""),
            ("numeric asset id", """{"id":"1.21.4","downloads":{"client":{"url":"https://piston-data.mojang.com/client.jar","size":6}},"assetIndex":{"id":42,"url":"https://launchermeta.mojang.com/assets.json"},"libraries":[]}"""),
            ("missing asset url", """{"id":"1.21.4","downloads":{"client":{"url":"https://piston-data.mojang.com/client.jar","size":6}},"assetIndex":{"id":"assets"},"libraries":[]}"""),
            ("numeric asset url", """{"id":"1.21.4","downloads":{"client":{"url":"https://piston-data.mojang.com/client.jar","size":6}},"assetIndex":{"id":"assets","url":42},"libraries":[]}""")
        };

        foreach (var fixture in fixtures)
        {
            var root = NewRoot(fixture.Label);
            try
            {
                var paths = new NexoPathService(root);
                var handler = new ContractHandler(fixture.Json);
                using var client = new HttpClient(handler);
                var installer = new MinecraftVanillaInstallService(
                    client,
                    paths,
                    new DownloadSourceService(),
                    TimeSpan.FromSeconds(2));
                var instance = Instance();

                try
                {
                    await installer.InstallAsync(instance, Version(handler.MetadataSha1));
                    throw new Exception(
                        $"Invalid metadata fixture '{fixture.Label}' unexpectedly prepared.");
                }
                catch (InvalidDataException)
                {
                }

                Assert(handler.MetadataRequests == 1,
                    $"Fixture '{fixture.Label}' should fetch version metadata once.");
                Assert(handler.DownstreamRequests == 0,
                    $"Fixture '{fixture.Label}' must fail before client/asset downloads.");
                Assert(
                    !File.Exists(Path.Combine(
                        paths.GetInstanceDirectory(instance.Id),
                        "install-state.json")),
                    $"Fixture '{fixture.Label}' must not publish prepared state.");
                Assert(
                    !File.Exists(Path.Combine(
                        paths.GetInstanceGameDirectory(instance.Id),
                        "versions",
                        "1.21.4",
                        "1.21.4.json")),
                    $"Fixture '{fixture.Label}' should quarantine/remove invalid version metadata.");
            }
            finally
            {
                TryDelete(root);
            }
        }
    }

    private static async Task TestInvalidRefreshPreservesExistingStateAsync()
    {
        var root = NewRoot("preserve-state");
        try
        {
            var paths = new NexoPathService(root);
            var instance = Instance();
            var instanceRoot = paths.GetInstanceDirectory(instance.Id);
            Directory.CreateDirectory(instanceRoot);
            var statePath = Path.Combine(instanceRoot, "install-state.json");
            var previous = Encoding.UTF8.GetBytes(
                "{\"id\":\"previous\",\"state\":\"prepared\"}");
            await File.WriteAllBytesAsync(statePath, previous);

            var handler = new ContractHandler(
                ValidMetadata("wrong-version"));
            using var client = new HttpClient(handler);
            var installer = new MinecraftVanillaInstallService(
                client,
                paths,
                new DownloadSourceService(),
                TimeSpan.FromSeconds(2));

            try
            {
                await installer.InstallAsync(instance, Version(handler.MetadataSha1));
                throw new Exception(
                    "Mismatched refresh unexpectedly succeeded.");
            }
            catch (InvalidDataException)
            {
            }

            var after = await File.ReadAllBytesAsync(statePath);
            Assert(previous.AsSpan().SequenceEqual(after),
                "Failed metadata refresh must preserve the previous prepared marker.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task TestValidMetadataPreparesAndResolvesAsync()
    {
        var root = NewRoot("valid");
        try
        {
            var paths = new NexoPathService(root);
            var handler = new ContractHandler(ValidMetadata("1.21.4"));
            using var client = new HttpClient(handler);
            var installer = new MinecraftVanillaInstallService(
                client,
                paths,
                new DownloadSourceService(),
                TimeSpan.FromSeconds(2));
            var instance = Instance();

            await installer.InstallAsync(instance, Version(handler.MetadataSha1));

            Assert(handler.MetadataRequests == 1,
                "Valid metadata should be fetched once.");
            Assert(handler.ClientRequests == 1,
                "Valid metadata should download the required client.");
            Assert(handler.AssetIndexRequests == 1,
                "Valid metadata should download the required asset index.");
            Assert(
                File.Exists(Path.Combine(
                    paths.GetInstanceDirectory(instance.Id),
                    "install-state.json")),
                "Valid metadata should publish prepared state.");

            using var resolved = await new MinecraftVersionMetadataResolver()
                .ResolveAsync(
                    paths.GetInstanceGameDirectory(instance.Id),
                    "1.21.4");
            Assert(
                resolved.Document.RootElement.GetProperty("id").GetString()
                    == "1.21.4",
                "Metadata accepted by Prepare should also satisfy resolver identity checks.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string ValidMetadata(string id)
        => "{\"id\":"
           + System.Text.Json.JsonSerializer.Serialize(id)
           + ",\"downloads\":{\"client\":{\"url\":\"https://piston-data.mojang.com/client.jar\",\"size\":6}},"
           + "\"assetIndex\":{\"id\":\"assets\",\"url\":\"https://launchermeta.mojang.com/assets.json\"},"
           + "\"libraries\":[]}";

    private static GameInstance Instance()
        => new(
            Guid.NewGuid().ToString("N"),
            "Vanilla metadata contract",
            "1.21.4",
            "vanilla",
            DateTimeOffset.UtcNow);

    private static MinecraftVersionInfo Version(string sha1)
        => new(
            "1.21.4",
            "release",
            "https://piston-meta.mojang.com/version-contract.json",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            sha1,
            0);

    private static string NewRoot(string suffix)
    {
        var safe = new string(
            suffix.Select(character =>
                    char.IsLetterOrDigit(character) ? character : '-')
                .ToArray());
        var root = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-vanilla-contract-tests",
            safe + "-" + Guid.NewGuid().ToString("N"));
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

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class ContractHandler(string metadataJson)
        : HttpMessageHandler
    {
        public int MetadataRequests { get; private set; }
        public int ClientRequests { get; private set; }
        public int AssetIndexRequests { get; private set; }
        public int DownstreamRequests
            => ClientRequests + AssetIndexRequests;
        public string MetadataSha1
            => Convert.ToHexString(
                    System.Security.Cryptography.SHA1.HashData(
                        Encoding.UTF8.GetBytes(metadataJson)))
                .ToLowerInvariant();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri
                ?? throw new InvalidOperationException("Missing URI.");

            if (uri.Host.Equals(
                    "piston-meta.mojang.com",
                    StringComparison.OrdinalIgnoreCase))
            {
                MetadataRequests++;
                return Task.FromResult(Text(metadataJson));
            }

            if (uri.Host.Equals(
                    "piston-data.mojang.com",
                    StringComparison.OrdinalIgnoreCase))
            {
                ClientRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(
                        Encoding.UTF8.GetBytes("client"))
                });
            }

            if (uri.Host.Equals(
                    "launchermeta.mojang.com",
                    StringComparison.OrdinalIgnoreCase))
            {
                AssetIndexRequests++;
                return Task.FromResult(Text("{\"objects\":{}}"));
            }

            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Text(string value)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    value,
                    Encoding.UTF8,
                    "application/json")
            };
    }
}
