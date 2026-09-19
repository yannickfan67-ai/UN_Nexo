using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class CurseForgeProviderRegression
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-curseforge-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var rootBytes = Encoding.UTF8.GetBytes("curseforge-root-mod");
            var dependencyBytes = Encoding.UTF8.GetBytes("curseforge-required-dependency");
            var handler = new CurseForgeHandler(rootBytes, dependencyBytes);
            using var client = new HttpClient(handler);
            var provider = new CurseForgeModProvider(
                client,
                new FixedKeyProvider("TEST-CURSEFORGE-KEY"),
                "yannickfan67-ai-UN_Nexo/test (github.com/yannickfan67-ai/UN_Nexo)");

            var projects = await provider.SearchAsync(
                "example",
                "1.21.4",
                "fabric",
                20);
            Assert(projects.Count == 1, "Expected one valid CurseForge search project.");
            Assert(projects[0].ProviderId == "curseforge", "CurseForge project provider id was not preserved.");
            Assert(projects[0].ProjectId == "100", "CurseForge project id was not parsed.");
            Assert(handler.LastSearchQuery.Contains("gameVersion=1.21.4", StringComparison.Ordinal),
                "CurseForge search must filter by Minecraft version.");
            Assert(handler.LastSearchQuery.Contains("modLoaderType=4", StringComparison.Ordinal),
                "Fabric search must use CurseForge loader type 4.");
            Assert(handler.ApiRequestsWithoutKey == 0,
                "Every official CurseForge API request must carry x-api-key.");
            Assert(handler.LastUserAgent.Contains("UN_Nexo", StringComparison.Ordinal),
                "CurseForge requests must carry the UN_Nexo user agent.");

            var latest = await provider.GetLatestCompatibleVersionAsync(
                "100",
                "1.21.4",
                "fabric")
                ?? throw new Exception("Expected a compatible CurseForge file.");
            Assert(latest.VersionId == "200", "Latest CurseForge file id was not parsed.");
            Assert(latest.SelectPrimaryFile().Sha1 == Sha1(rootBytes),
                "CurseForge SHA-1 metadata was not retained.");
            Assert(
                latest.Dependencies.Count == 1
                && latest.Dependencies[0].ProjectId == "101"
                && latest.Dependencies[0].Type == ModProviderDependencyType.Required,
                "Required CurseForge dependency relation was not parsed.");

            var plan = await new ModDependencyPlanner(provider).BuildAsync(
                projects[0],
                latest,
                "1.21.4",
                "fabric");
            Assert(
                plan.InstallOrder.Select(item => item.Project.ProjectId)
                    .SequenceEqual(["101", "100"]),
                "CurseForge required dependency must be planned before the root mod.");

            var paths = new NexoPathService(root);
            var mods = new InstanceModService(paths);
            var instanceId = Guid.NewGuid().ToString("N");
            var result = await new ModDependencyInstaller(provider, mods).InstallAsync(
                instanceId,
                plan,
                new Dictionary<string, ModProviderInstalledMatch>());

            Assert(result.Installed.Count == 2,
                "CurseForge dependency install should atomically publish dependency and root.");
            Assert(File.Exists(Path.Combine(mods.GetModsDirectory(instanceId), "dependency.jar")),
                "Required CurseForge dependency JAR was not published.");
            Assert(File.Exists(Path.Combine(mods.GetModsDirectory(instanceId), "root.jar")),
                "Root CurseForge JAR was not published.");
            Assert(handler.CdnRequestsWithApiKey == 0,
                "CurseForge API key must never be sent to forgecdn.net.");
            Assert(handler.ApiRequestsWithoutKey == 0,
                "CurseForge API key must remain present on official API calls.");

            var matches = await provider.MatchInstalledAsync(
                mods.GetModsDirectory(instanceId),
                mods.List(instanceId));
            Assert(matches.Count == 0,
                "Empty exact-match fixture should return no installed CurseForge matches.");
            Assert(handler.FingerprintRequests == 1,
                "Physical installed JARs should produce one CurseForge fingerprint request.");
            Assert(handler.LastFingerprintCount == 2,
                "Both physical installed JARs should be fingerprinted.");

            var linkedInstanceId = Guid.NewGuid().ToString("N");
            var linkedMods = mods.GetModsDirectory(linkedInstanceId);
            Directory.CreateDirectory(linkedMods);
            var outside = Path.Combine(root, "outside.jar");
            await File.WriteAllBytesAsync(outside, Encoding.UTF8.GetBytes("outside"));
            var linked = Path.Combine(linkedMods, "linked.jar");
            var linkedSupported = true;
            try
            {
                File.CreateSymbolicLink(linked, outside);
            }
            catch (Exception ex) when (
                ex is UnauthorizedAccessException
                or IOException
                or PlatformNotSupportedException
                or NotSupportedException)
            {
                linkedSupported = false;
                Console.WriteLine("SKIP CurseForge linked-JAR fixture: " + ex.GetType().Name);
            }

            if (linkedSupported)
            {
                var before = handler.FingerprintRequests;
                var linkedMatches = await provider.MatchInstalledAsync(
                    linkedMods,
                    [
                        new InstalledMod(
                            "linked.jar",
                            true,
                            new FileInfo(outside).Length,
                            DateTimeOffset.UtcNow)
                    ]);
                Assert(linkedMatches.Count == 0,
                    "Linked CurseForge JAR must not be treated as an installed provider match.");
                Assert(handler.FingerprintRequests == before,
                    "Linked-only installed set must not reach the CurseForge fingerprint API.");
            }

            handler.RedirectRootDownloadToUntrusted = true;
            var rejectedId = Guid.NewGuid().ToString("N");
            try
            {
                await provider.InstallAsync(
                    rejectedId,
                    projects[0],
                    latest,
                    null,
                    mods);
                throw new Exception("Untrusted CurseForge CDN redirect unexpectedly installed.");
            }
            catch (InvalidDataException ex)
            {
                Assert(
                    ex.Message.Contains("forgecdn", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("trusted", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("redirect", StringComparison.OrdinalIgnoreCase),
                    "Untrusted redirect rejection should explain the CDN trust boundary.");
            }
            Assert(handler.UntrustedRequests == 0,
                "Nexo must reject an untrusted CurseForge redirect before connecting to it.");
            Assert(mods.List(rejectedId).Count == 0,
                "Rejected CurseForge redirect must not publish a mod.");
            handler.RedirectRootDownloadToUntrusted = false;

            handler.CorruptRootDownload = true;
            var corruptId = Guid.NewGuid().ToString("N");
            try
            {
                await provider.InstallAsync(
                    corruptId,
                    projects[0],
                    latest,
                    null,
                    mods);
                throw new Exception("Corrupt CurseForge download unexpectedly installed.");
            }
            catch (InvalidDataException ex)
            {
                Assert(ex.Message.Contains("SHA-1", StringComparison.OrdinalIgnoreCase),
                    "CurseForge checksum failure should be actionable.");
            }
            Assert(mods.List(corruptId).Count == 0,
                "Failed CurseForge checksum verification must not publish a mod.");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static string Sha1(byte[] bytes)
        => Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class FixedKeyProvider(string key) : ICurseForgeApiKeyProvider
    {
        public bool IsConfigured => true;

        public ValueTask<string> GetApiKeyAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(key);
        }
    }

    private sealed class CurseForgeHandler(
        byte[] rootBytes,
        byte[] dependencyBytes) : HttpMessageHandler
    {
        private readonly string _rootSha1 = Sha1(rootBytes);
        private readonly string _dependencySha1 = Sha1(dependencyBytes);

        public string LastSearchQuery { get; private set; } = string.Empty;
        public string LastUserAgent { get; private set; } = string.Empty;
        public int ApiRequestsWithoutKey { get; private set; }
        public int CdnRequestsWithApiKey { get; private set; }
        public int FingerprintRequests { get; private set; }
        public int LastFingerprintCount { get; private set; }
        public int UntrustedRequests { get; private set; }
        public bool RedirectRootDownloadToUntrusted { get; set; }
        public bool CorruptRootDownload { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri
                ?? throw new InvalidOperationException("Missing request URI.");
            LastUserAgent = request.Headers.UserAgent.ToString();

            if (uri.Host.Equals("api.curseforge.com", StringComparison.OrdinalIgnoreCase))
            {
                if (!request.Headers.TryGetValues("x-api-key", out var keys)
                    || !keys.Contains("TEST-CURSEFORGE-KEY", StringComparer.Ordinal))
                {
                    ApiRequestsWithoutKey++;
                }

                if (uri.AbsolutePath.Equals("/v1/games", StringComparison.Ordinal))
                {
                    return Json(
                        """{"data":[{"id":432,"name":"Minecraft","slug":"minecraft"}]}""");
                }

                if (uri.AbsolutePath.Equals("/v1/categories", StringComparison.Ordinal))
                {
                    return Json(
                        """{"data":[{"id":6,"name":"Mods","slug":"mc-mods","isClass":true}]}""");
                }

                if (uri.AbsolutePath.Equals("/v1/mods/search", StringComparison.Ordinal))
                {
                    LastSearchQuery = uri.Query.TrimStart('?');
                    return Json(
                        """
                        {
                          "data":[{
                            "id":100,
                            "name":"Example Root",
                            "slug":"example-root",
                            "summary":"Root test mod",
                            "downloadCount":1234,
                            "authors":[{"name":"Tester"}],
                            "logo":{"thumbnailUrl":"https://example.invalid/icon.png"}
                          }]
                        }
                        """);
                }

                if (uri.AbsolutePath.Equals("/v1/mods/101", StringComparison.Ordinal))
                {
                    return Json(
                        """
                        {"data":{"id":101,"name":"Example Dependency","slug":"example-dependency","summary":"Dependency","downloadCount":50,"authors":[{"name":"Tester"}]}}
                        """);
                }

                if (uri.AbsolutePath.Equals("/v1/mods/100/files", StringComparison.Ordinal))
                    return Json(FileListJson(100, 200, "root.jar", rootBytes.LongLength, _rootSha1, requiredDependency: 101));

                if (uri.AbsolutePath.Equals("/v1/mods/101/files", StringComparison.Ordinal))
                    return Json(FileListJson(101, 201, "dependency.jar", dependencyBytes.LongLength, _dependencySha1, requiredDependency: null));

                if (uri.AbsolutePath.Equals("/v1/mods/100/files/200/download-url", StringComparison.Ordinal))
                {
                    return Json(
                        """{"data":"https://edge.forgecdn.net/files/200/root.jar"}""");
                }

                if (uri.AbsolutePath.Equals("/v1/mods/101/files/201/download-url", StringComparison.Ordinal))
                {
                    return Json(
                        """{"data":"https://edge.forgecdn.net/files/201/dependency.jar"}""");
                }

                if (uri.AbsolutePath.Equals("/v1/fingerprints/432", StringComparison.Ordinal))
                {
                    FingerprintRequests++;
                    var json = request.Content is null
                        ? "{}"
                        : await request.Content.ReadAsStringAsync(cancellationToken);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("fingerprints", out var fingerprints)
                        && fingerprints.ValueKind == JsonValueKind.Array)
                    {
                        LastFingerprintCount = fingerprints.GetArrayLength();
                    }

                    return Json(
                        """{"data":{"exactMatches":[],"exactFingerprints":[],"partialMatches":[],"partialMatchFingerprints":{},"installedFingerprints":[],"unmatchedFingerprints":[]}}""");
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (uri.Host.Equals("edge.forgecdn.net", StringComparison.OrdinalIgnoreCase))
            {
                if (request.Headers.Contains("x-api-key"))
                    CdnRequestsWithApiKey++;

                if (uri.AbsolutePath.EndsWith("/root.jar", StringComparison.Ordinal))
                {
                    if (RedirectRootDownloadToUntrusted)
                    {
                        var response = new HttpResponseMessage(HttpStatusCode.Found);
                        response.Headers.Location = new Uri("https://example.invalid/evil.jar");
                        return response;
                    }

                    var bytes = CorruptRootDownload
                        ? Enumerable.Repeat((byte)0xEE, rootBytes.Length).ToArray()
                        : rootBytes;
                    return Bytes(bytes);
                }

                if (uri.AbsolutePath.EndsWith("/dependency.jar", StringComparison.Ordinal))
                    return Bytes(dependencyBytes);

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            UntrustedRequests++;
            return Bytes(rootBytes);
        }

        private static string FileListJson(
            int modId,
            int fileId,
            string fileName,
            long length,
            string sha1,
            int? requiredDependency)
        {
            var dependency = requiredDependency is null
                ? "[]"
                : $"[{{\"modId\":{requiredDependency.Value},\"relationType\":3}}]";
            return
                $"{{\"data\":[{{\"id\":{fileId},\"modId\":{modId},\"isAvailable\":true,"
                + $"\"displayName\":\"{fileName}\",\"fileName\":\"{fileName}\","
                + $"\"fileDate\":\"2026-09-19T00:00:00Z\",\"fileLength\":{length},"
                + $"\"hashes\":[{{\"value\":\"{sha1}\",\"algo\":1}}],"
                + "\"gameVersions\":[\"1.21.4\",\"Fabric\"],"
                + $"\"dependencies\":{dependency}}}]}}";
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
}
