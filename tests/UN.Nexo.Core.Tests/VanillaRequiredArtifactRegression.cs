using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UN.Nexo.Core.Launching;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class VanillaRequiredArtifactRegression
{
    private const string VersionId = "required-artifacts";
    private const string AssetId = "required-assets";
    private const string MavenPath =
        "/com/example/legacy/1.0/legacy-1.0.jar";
    private const string LogPath =
        "/log_configs/client-log.xml";

    internal static async Task RunAsync()
    {
        await TestMavenFallbackAndLoggingAsync();
        await TestLoggingBadShaRejectedAsync();
        await TestShaCacheIntegrityAsync();
        await TestHashlessSizeValidationAsync();
        await TestMalformedShaRejectedBeforeArtifactRequestAsync();
    }

    private static async Task TestMavenFallbackAndLoggingAsync()
    {
        var root = NewRoot();
        try
        {
            var client = Encoding.UTF8.GetBytes(
                "verified-client-payload");
            var maven = Encoding.UTF8.GetBytes(
                "legacy-maven-library");
            var logging = Encoding.UTF8.GetBytes(
                "<Configuration status=\"WARN\"/>");
            var assetIndex = Encoding.UTF8.GetBytes(
                "{\"objects\":{}}");
            var metadata = BuildMetadata(
                client,
                assetIndex,
                clientSha1: Sha1(client),
                loggingBytes: logging,
                loggingSha1: Sha1(logging),
                includeMavenFallback: true);

            var handler = new RoutingHandler(
                metadata,
                client,
                assetIndex,
                maven,
                logging);
            using var http = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            var paths = new NexoPathService(root);
            var instance = Instance();
            var service = new MinecraftVanillaInstallService(
                http,
                paths,
                new DownloadSourceService(),
                TimeSpan.FromSeconds(1));

            await service.InstallAsync(
                instance,
                Version(metadata));

            Assert(handler.Requests.Any(path =>
                    path.EndsWith(
                        MavenPath + ".sha1",
                        StringComparison.Ordinal)),
                "Maven-name-only library should fetch a checksum sidecar.");
            Assert(handler.Requests.Any(path =>
                    path.EndsWith(
                        MavenPath,
                        StringComparison.Ordinal)),
                "Maven-name-only library should download the derived artifact path.");
            Assert(handler.Requests.Any(path =>
                    path.EndsWith(
                        LogPath,
                        StringComparison.Ordinal)),
                "Prepare should download the declared logging configuration.");

            var gameRoot =
                paths.GetInstanceGameDirectory(instance.Id);
            var mavenFile = Path.Combine(
                gameRoot,
                "libraries",
                MavenPath.TrimStart('/')
                    .Replace(
                        '/',
                        Path.DirectorySeparatorChar));
            var logFile = Path.Combine(
                gameRoot,
                "assets",
                "log_configs",
                "client-log.xml");
            Assert(File.Exists(mavenFile),
                "Derived Maven library path was not materialized.");
            Assert(File.Exists(logFile),
                "Logging configuration was not materialized.");

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
                "test");
            var account = new LauncherAccount(
                "offline:required-artifacts",
                "offline",
                "ArtifactUser",
                Guid.NewGuid().ToString("D"),
                DateTimeOffset.UtcNow);

            var plan = await new MinecraftLaunchPlanBuilder(paths)
                .BuildAsync(
                    instance,
                    account,
                    [java]);
            Assert(plan.Arguments.Any(argument =>
                    argument.Contains(
                        "-Dlog4j.configurationFile=",
                        StringComparison.Ordinal)
                    && argument.Contains(
                        logFile,
                        StringComparison.Ordinal)),
                "Launch should include the prepared logging configuration argument.");

            var corrupted = logging.ToArray();
            corrupted[0] ^= 0x20;
            await File.WriteAllBytesAsync(
                logFile,
                corrupted);
            try
            {
                _ = await new MinecraftLaunchPlanBuilder(paths)
                    .BuildAsync(
                        instance,
                        account,
                        [java]);
                throw new Exception(
                    "Corrupted logging configuration unexpectedly remained launchable.");
            }
            catch (FileNotFoundException)
            {
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task TestLoggingBadShaRejectedAsync()
    {
        var root = NewRoot();
        try
        {
            var client = Encoding.UTF8.GetBytes("client");
            var assets = Encoding.UTF8.GetBytes(
                "{\"objects\":{}}");
            var expectedLog =
                Encoding.UTF8.GetBytes("expected-log-config");
            var badLog =
                Encoding.UTF8.GetBytes("corrupted-log-file");
            if (badLog.Length != expectedLog.Length)
                Array.Resize(
                    ref badLog,
                    expectedLog.Length);

            var metadata = BuildMetadata(
                client,
                assets,
                Sha1(client),
                expectedLog,
                Sha1(expectedLog));
            var handler = new RoutingHandler(
                metadata,
                client,
                assets,
                Encoding.UTF8.GetBytes("unused"),
                badLog);
            using var http = new HttpClient(handler);
            var paths = new NexoPathService(root);
            var instance = Instance();
            var service = new MinecraftVanillaInstallService(
                http,
                paths,
                new DownloadSourceService());

            try
            {
                await service.InstallAsync(
                    instance,
                    Version(metadata));
                throw new Exception(
                    "Bad logging SHA-1 unexpectedly prepared.");
            }
            catch (InvalidDataException)
            {
            }

            Assert(!File.Exists(Path.Combine(
                    paths.GetInstanceDirectory(instance.Id),
                    "install-state.json")),
                "Bad logging configuration must not publish prepared state.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task TestShaCacheIntegrityAsync()
    {
        var root = NewRoot();
        try
        {
            var client = Encoding.UTF8.GetBytes(
                "sha-verified-client");
            var assets = Encoding.UTF8.GetBytes(
                "{\"objects\":{}}");
            var metadata = BuildMetadata(
                client,
                assets,
                Sha1(client));
            var handler = new RoutingHandler(
                metadata,
                client,
                assets);
            using var http = new HttpClient(handler);
            var paths = new NexoPathService(root);
            var instance = Instance();
            var clientPath = ClientPath(
                paths,
                instance);
            Directory.CreateDirectory(
                Path.GetDirectoryName(clientPath)!);
            var corrupt = client.ToArray();
            corrupt[0] ^= 0x5A;
            await File.WriteAllBytesAsync(
                clientPath,
                corrupt);

            var service = new MinecraftVanillaInstallService(
                http,
                paths,
                new DownloadSourceService());
            await service.InstallAsync(
                instance,
                Version(metadata));

            Assert(handler.ClientRequests == 1,
                "Same-size wrong-SHA client cache must be replaced.");
            var replacedClient =
                await File.ReadAllBytesAsync(clientPath);
            Assert(client.SequenceEqual(replacedClient),
                "Wrong-SHA client cache was not replaced.");

            await service.InstallAsync(
                instance,
                Version(metadata));
            Assert(handler.ClientRequests == 1,
                "Valid SHA-1 client cache should be reused.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task TestHashlessSizeValidationAsync()
    {
        var root = NewRoot();
        try
        {
            var client = Encoding.UTF8.GetBytes(
                "size-only-client");
            var assets = Encoding.UTF8.GetBytes(
                "{\"objects\":{}}");
            var metadata = BuildMetadata(
                client,
                assets,
                clientSha1: null);
            var handler = new RoutingHandler(
                metadata,
                client,
                assets);
            using var http = new HttpClient(handler);
            var paths = new NexoPathService(root);
            var instance = Instance();
            var clientPath = ClientPath(
                paths,
                instance);
            Directory.CreateDirectory(
                Path.GetDirectoryName(clientPath)!);
            await File.WriteAllBytesAsync(
                clientPath,
                [1]);

            var service = new MinecraftVanillaInstallService(
                http,
                paths,
                new DownloadSourceService());
            await service.InstallAsync(
                instance,
                Version(metadata));

            Assert(handler.ClientRequests == 1,
                "Wrong-size hashless cache must not be accepted.");
            var sizeValidatedClient =
                await File.ReadAllBytesAsync(clientPath);
            Assert(client.SequenceEqual(sizeValidatedClient),
                "Size-validated client was not downloaded correctly.");
        }
        finally
        {
            TryDelete(root);
        }

        root = NewRoot();
        try
        {
            var client = Encoding.UTF8.GetBytes(
                "declared-client-size");
            var assets = Encoding.UTF8.GetBytes(
                "{\"objects\":{}}");
            var metadata = BuildMetadata(
                client,
                assets,
                clientSha1: null);
            var handler = new RoutingHandler(
                metadata,
                client,
                assets)
            {
                TruncateClient = true
            };
            using var http = new HttpClient(handler);
            var paths = new NexoPathService(root);
            var instance = Instance();
            var service = new MinecraftVanillaInstallService(
                http,
                paths,
                new DownloadSourceService());

            try
            {
                await service.InstallAsync(
                    instance,
                    Version(metadata));
                throw new Exception(
                    "Truncated hashless client response unexpectedly prepared.");
            }
            catch (InvalidDataException)
            {
            }

            Assert(!File.Exists(Path.Combine(
                    paths.GetInstanceDirectory(instance.Id),
                    "install-state.json")),
                "Truncated hashless body must not publish prepared state.");
            Assert(!File.Exists(ClientPath(
                    paths,
                    instance)),
                "Truncated hashless body must not publish the client JAR.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task TestMalformedShaRejectedBeforeArtifactRequestAsync()
    {
        var root = NewRoot();
        try
        {
            var client = Encoding.UTF8.GetBytes("client");
            var assets = Encoding.UTF8.GetBytes(
                "{\"objects\":{}}");
            var metadata = BuildMetadata(
                client,
                assets,
                clientSha1: "not-a-sha1");
            var handler = new RoutingHandler(
                metadata,
                client,
                assets);
            using var http = new HttpClient(handler);
            var paths = new NexoPathService(root);
            var instance = Instance();
            var service = new MinecraftVanillaInstallService(
                http,
                paths,
                new DownloadSourceService());

            try
            {
                await service.InstallAsync(
                    instance,
                    Version(metadata));
                throw new Exception(
                    "Malformed client SHA-1 unexpectedly prepared.");
            }
            catch (InvalidDataException)
            {
            }

            Assert(handler.ClientRequests == 0,
                "Malformed digest should be rejected before the client request.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string BuildMetadata(
        byte[] client,
        byte[] assetIndex,
        string? clientSha1,
        byte[]? loggingBytes = null,
        string? loggingSha1 = null,
        bool includeMavenFallback = false)
    {
        var clientMetadata = new Dictionary<string, object?>
        {
            ["url"] =
                "https://piston-data.mojang.com/client.jar",
            ["size"] = client.LongLength
        };
        if (clientSha1 is not null)
            clientMetadata["sha1"] = clientSha1;

        var libraries = includeMavenFallback
            ? new object[]
            {
                new
                {
                    name = "com.example:legacy:1.0",
                    url = "https://libraries.minecraft.net/"
                }
            }
            : [];

        object? logging = null;
        if (loggingBytes is not null)
        {
            logging = new
            {
                client = new
                {
                    argument =
                        "-Dlog4j.configurationFile=${path}",
                    file = new
                    {
                        id = "client-log.xml",
                        url =
                            "https://launcher.mojang.com/log_configs/client-log.xml",
                        sha1 = loggingSha1,
                        size = loggingBytes.LongLength
                    }
                }
            };
        }

        return JsonSerializer.Serialize(
            new
            {
                id = VersionId,
                type = "release",
                mainClass =
                    "net.minecraft.client.main.Main",
                javaVersion = new
                {
                    majorVersion = 21
                },
                downloads = new
                {
                    client = clientMetadata
                },
                assetIndex = new
                {
                    id = AssetId,
                    url =
                        "https://launchermeta.mojang.com/assets.json",
                    sha1 = Sha1(assetIndex),
                    size = assetIndex.LongLength
                },
                libraries,
                logging,
                arguments = new
                {
                    jvm = new[]
                    {
                        "-cp",
                        "${classpath}"
                    },
                    game = Array.Empty<string>()
                }
            },
            new JsonSerializerOptions
            {
                DefaultIgnoreCondition =
                    System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
    }

    private static GameInstance Instance()
        => new(
            Guid.NewGuid().ToString("N"),
            "Required artifact fixture",
            VersionId,
            "vanilla",
            DateTimeOffset.UtcNow);

    private static MinecraftVersionInfo Version(
        string metadata)
        => new(
            VersionId,
            "release",
            "https://piston-meta.mojang.com/version.json",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            string.Empty,
            0);

    private static string ClientPath(
        NexoPathService paths,
        GameInstance instance)
        => Path.Combine(
            paths.GetInstanceGameDirectory(instance.Id),
            "versions",
            VersionId,
            VersionId + ".jar");

    private static string Sha1(byte[] bytes)
        => Convert.ToHexString(
                SHA1.HashData(bytes))
            .ToLowerInvariant();

    private static string NewRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-vanilla-required-artifacts",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(
                    root,
                    recursive: true);
        }
        catch
        {
        }
    }

    private static void Assert(
        bool condition,
        string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class RoutingHandler(
        string metadata,
        byte[] client,
        byte[] assetIndex,
        byte[]? maven = null,
        byte[]? logging = null)
        : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public int ClientRequests { get; private set; }
        public bool TruncateClient { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri
                ?? throw new InvalidOperationException(
                    "Missing request URI.");
            Requests.Add(uri.AbsolutePath);

            if (uri.AbsolutePath.EndsWith(
                    "/version.json",
                    StringComparison.Ordinal))
                return Task.FromResult(Json(metadata));

            if (uri.AbsolutePath.EndsWith(
                    "/client.jar",
                    StringComparison.Ordinal))
            {
                ClientRequests++;
                var body = TruncateClient
                    ? client[..Math.Max(1, client.Length / 2)]
                    : client;
                var content = new ByteArrayContent(body);
                if (TruncateClient)
                    content.Headers.ContentLength =
                        client.LongLength;
                return Task.FromResult(new HttpResponseMessage(
                    HttpStatusCode.OK)
                {
                    Content = content
                });
            }

            if (uri.AbsolutePath.EndsWith(
                    "/assets.json",
                    StringComparison.Ordinal))
                return Task.FromResult(Bytes(
                    assetIndex,
                    "application/json"));

            if (uri.AbsolutePath.EndsWith(
                    MavenPath + ".sha1",
                    StringComparison.Ordinal)
                && maven is not null)
            {
                return Task.FromResult(new HttpResponseMessage(
                    HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        Sha1(maven) + "  legacy-1.0.jar\n",
                        Encoding.ASCII,
                        "text/plain")
                });
            }

            if (uri.AbsolutePath.EndsWith(
                    MavenPath,
                    StringComparison.Ordinal)
                && maven is not null)
                return Task.FromResult(Bytes(
                    maven,
                    "application/java-archive"));

            if (uri.AbsolutePath.EndsWith(
                    LogPath,
                    StringComparison.Ordinal)
                && logging is not null)
                return Task.FromResult(Bytes(
                    logging,
                    "application/xml"));

            return Task.FromResult(
                new HttpResponseMessage(
                    HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(
            string value)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    value,
                    Encoding.UTF8,
                    "application/json")
            };

        private static HttpResponseMessage Bytes(
            byte[] value,
            string mediaType)
        {
            var content = new ByteArrayContent(value);
            content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue(
                    mediaType);
            return new HttpResponseMessage(
                HttpStatusCode.OK)
            {
                Content = content
            };
        }
    }
}
