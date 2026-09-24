using System.Net;
using System.Security.Cryptography;
using System.Text;
using UN.Nexo.Core.Launching;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class NetworkTargetRegression
{
    internal static async Task RunAsync()
    {
        TestMinecraftServerTargetParsing();

        var sources = new DownloadSourceService();

        var official = sources.GetCandidates(
            "http://piston-meta.mojang.com/v1/packages/test.json");
        Assert(
            official.Count == 1
            && official[0].StartsWith("https://piston-meta.mojang.com/", StringComparison.Ordinal),
            "Known legacy Mojang HTTP URLs should be upgraded to HTTPS.");

        var fabric = sources.GetCandidates(
            "https://maven.fabricmc.net/net/fabricmc/fabric-loader/0.16.9/fabric-loader-0.16.9.jar");
        Assert(
            fabric.Count == 1
            && fabric[0].StartsWith("https://maven.fabricmc.net/", StringComparison.Ordinal),
            "Fabric Maven HTTPS origin should remain trusted.");

        foreach (var url in new[]
        {
            "http://127.0.0.1/private",
            "https://127.0.0.1/private",
            "https://10.0.0.1/private",
            "https://169.254.169.254/latest/meta-data/",
            "https://localhost/private",
            "https://example.com/not-a-launcher-origin"
        })
        {
            try
            {
                _ = sources.GetCandidates(url);
                throw new Exception($"Untrusted metadata URL should be rejected: {url}");
            }
            catch (InvalidDataException)
            {
            }
        }

        await TestPrivateRedirectRejectedBeforeFollowAsync();
        await TestPrivateArtifactRejectedBeforeRequestAsync();
        await TestTrustedRedirectStillWorksAsync();
    }

    private static void TestMinecraftServerTargetParsing()
    {
        var rawIpv6 = MinecraftServerTarget.Parse("2001:db8::1");
        Assert(rawIpv6.Host == "2001:db8::1"
               && rawIpv6.Port == MinecraftServerTarget.DefaultPort
               && !rawIpv6.HasExplicitPort,
            "Valid unbracketed IPv6 should use the default port.");

        var bracketedIpv6 = MinecraftServerTarget.Parse("[2001:db8::1]:25570");
        Assert(bracketedIpv6.Host == "2001:db8::1"
               && bracketedIpv6.Port == 25570
               && bracketedIpv6.HasExplicitPort,
            "Bracketed IPv6 with an explicit port should remain supported.");

        var hostname = MinecraftServerTarget.Parse("play.example:25566");
        Assert(hostname.Host == "play.example"
               && hostname.Port == 25566
               && hostname.HasExplicitPort,
            "Normal hostname:port parsing should remain unchanged.");

        foreach (var malformed in new[] { "not:an:ipv6", "example.com:25565:garbage" })
        {
            try
            {
                _ = MinecraftServerTarget.Parse(malformed);
                throw new Exception($"Malformed multi-colon server target should be rejected: {malformed}");
            }
            catch (FormatException)
            {
            }
        }
    }

    private static async Task TestPrivateRedirectRejectedBeforeFollowAsync()
    {
        var root = NewRoot();
        try
        {
            var handler = new TargetPolicyHandler(TargetScenario.PrivateRedirect);
            using var client = new HttpClient(handler);
            var paths = new NexoPathService(root);
            var installer = new MinecraftVanillaInstallService(
                client,
                paths,
                new DownloadSourceService(),
                TimeSpan.FromSeconds(2));
            var id = Guid.NewGuid().ToString("N");
            var instance = new GameInstance(id, "Private redirect", "redirect-test", "vanilla", DateTimeOffset.UtcNow);
            var version = Version(
                "redirect-test",
                "https://piston-meta.mojang.com/redirect-test.json");

            try
            {
                await installer.InstallAsync(instance, version);
                throw new Exception("Private redirect target should be rejected.");
            }
            catch (InvalidDataException)
            {
            }

            Assert(handler.PrivateRequests == 0,
                "Private redirect target must be rejected before a request is sent.");
            Assert(!File.Exists(Path.Combine(paths.GetInstanceDirectory(instance.Id), "install-state.json")),
                "Rejected redirect must not publish prepared state.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task TestPrivateArtifactRejectedBeforeRequestAsync()
    {
        var root = NewRoot();
        try
        {
            var handler = new TargetPolicyHandler(TargetScenario.PrivateArtifact);
            using var client = new HttpClient(handler);
            var paths = new NexoPathService(root);
            var installer = new MinecraftVanillaInstallService(
                client,
                paths,
                new DownloadSourceService(),
                TimeSpan.FromSeconds(2));
            var id = Guid.NewGuid().ToString("N");
            var instance = new GameInstance(id, "Private artifact", "artifact-test", "vanilla", DateTimeOffset.UtcNow);
            var version = Version(
                "artifact-test",
                "https://piston-meta.mojang.com/artifact-test.json",
                handler.MetadataSha1);

            try
            {
                await installer.InstallAsync(instance, version);
                throw new Exception("Private library artifact should be rejected.");
            }
            catch (InvalidDataException)
            {
            }

            Assert(handler.PrivateRequests == 0,
                "Private artifact target must be rejected before a request is sent.");
            Assert(!File.Exists(Path.Combine(paths.GetInstanceDirectory(instance.Id), "install-state.json")),
                "Rejected artifact URL must not publish prepared state.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task TestTrustedRedirectStillWorksAsync()
    {
        var root = NewRoot();
        try
        {
            var handler = new TargetPolicyHandler(TargetScenario.TrustedRedirect);
            using var client = new HttpClient(handler);
            var paths = new NexoPathService(root);
            var installer = new MinecraftVanillaInstallService(
                client,
                paths,
                new DownloadSourceService(),
                TimeSpan.FromSeconds(2));
            var id = Guid.NewGuid().ToString("N");
            var instance = new GameInstance(id, "Trusted redirect", "trusted-redirect", "vanilla", DateTimeOffset.UtcNow);
            var version = Version(
                "trusted-redirect",
                "https://piston-meta.mojang.com/trusted-redirect.json",
                handler.MetadataSha1);

            await installer.InstallAsync(instance, version);

            Assert(handler.TrustedRedirectRequests == 1,
                "Trusted redirect destination should be requested exactly once.");
            Assert(File.Exists(Path.Combine(paths.GetInstanceDirectory(instance.Id), "install-state.json")),
                "Trusted redirect should still allow normal preparation.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static MinecraftVersionInfo Version(
        string id,
        string url,
        string sha1 = "0000000000000000000000000000000000000000")
        => new(
            id,
            "release",
            url,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            sha1,
            0);

    private static string NewRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-target-policy-tests",
            Guid.NewGuid().ToString("N"));
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

    private enum TargetScenario
    {
        PrivateRedirect,
        PrivateArtifact,
        TrustedRedirect
    }

    private sealed class TargetPolicyHandler(TargetScenario scenario) : HttpMessageHandler
    {
        private const string TrustedRedirectMetadata =
            "{\"id\":\"trusted-redirect\","
            + "\"downloads\":{\"client\":{\"url\":\"https://piston-data.mojang.com/client.jar\",\"size\":6}},"
            + "\"assetIndex\":{\"id\":\"trusted-assets\",\"url\":\"https://launchermeta.mojang.com/assets.json\"},"
            + "\"libraries\":[]}";
        private const string PrivateArtifactMetadata =
            "{\"id\":\"artifact-test\","
            + "\"downloads\":{\"client\":{\"url\":\"https://piston-data.mojang.com/client.jar\",\"size\":6}},"
            + "\"assetIndex\":{\"id\":\"artifact-assets\",\"url\":\"https://launchermeta.mojang.com/artifact-assets.json\"},"
            + "\"libraries\":[{\"name\":\"example:private:1.0\",\"downloads\":{\"artifact\":{"
            + "\"path\":\"example/private/1.0/private-1.0.jar\","
            + "\"url\":\"https://127.0.0.1/private.jar\"}}}]}";

        public int PrivateRequests { get; private set; }
        public int TrustedRedirectRequests { get; private set; }
        public string MetadataSha1
            => Convert.ToHexString(
                    SHA1.HashData(
                        Encoding.UTF8.GetBytes(
                            scenario == TargetScenario.TrustedRedirect
                                ? TrustedRedirectMetadata
                                : PrivateArtifactMetadata)))
                .ToLowerInvariant();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Missing request URI.");

            if (uri.IsLoopback
                || uri.Host is "10.0.0.1" or "169.254.169.254" or "localhost")
            {
                PrivateRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("private")
                });
            }

            if (scenario == TargetScenario.PrivateRedirect
                && uri.Host.Equals("piston-meta.mojang.com", StringComparison.OrdinalIgnoreCase))
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("http://127.0.0.1/private");
                return Task.FromResult(redirect);
            }

            if (scenario == TargetScenario.TrustedRedirect
                && uri.Host.Equals("piston-meta.mojang.com", StringComparison.OrdinalIgnoreCase))
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri(
                    "https://launchermeta.mojang.com/trusted-redirect.json");
                return Task.FromResult(redirect);
            }

            if (scenario == TargetScenario.TrustedRedirect
                && uri.Host.Equals("launchermeta.mojang.com", StringComparison.OrdinalIgnoreCase))
            {
                if (uri.AbsolutePath.Equals(
                        "/trusted-redirect.json",
                        StringComparison.OrdinalIgnoreCase))
                {
                    TrustedRedirectRequests++;
                    return Task.FromResult(Json(
                        TrustedRedirectMetadata));
                }

                if (uri.AbsolutePath.Equals(
                        "/assets.json",
                        StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(Json("{\"objects\":{}}"));
            }

            if (uri.Host.Equals("piston-data.mojang.com", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes("client"))
                });
            }

            if (scenario == TargetScenario.PrivateArtifact
                && uri.Host.Equals("piston-meta.mojang.com", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(Json(
                    PrivateArtifactMetadata));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(string value)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(value, Encoding.UTF8, "application/json")
            };
    }
}
