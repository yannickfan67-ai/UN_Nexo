using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class ModrinthProviderRegression
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "un-nexo-modrinth-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var payload = new byte[] { 1, 2, 3, 4, 5 };
            var handler = new ModrinthHandler(payload);
            using var client = new HttpClient(handler);
            var provider = new ModrinthModProvider(
                client,
                "yannickfan67-ai-UN_Nexo/test (github.com/yannickfan67-ai/UN_Nexo)");

            var projects = await provider.SearchAsync("sodium", "1.21.4", "fabric");
            Assert(projects.Count == 1, "Malformed Modrinth search hits should be skipped.");
            Assert(projects[0].ProjectId == "AABBCCDD", "Expected Modrinth project was not parsed.");
            Assert(handler.LastUserAgent.Contains("UN_Nexo", StringComparison.Ordinal),
                "Modrinth requests must carry a unique UN_Nexo user agent.");
            Assert(handler.LastSearchQuery.Contains("versions%3A1.21.4", StringComparison.OrdinalIgnoreCase)
                   || handler.LastSearchQuery.Contains("1.21.4", StringComparison.Ordinal),
                "Minecraft version facet was not sent to Modrinth.");
            Assert(handler.LastSearchQuery.Contains("fabric", StringComparison.OrdinalIgnoreCase),
                "Loader facet was not sent to Modrinth.");

            var latest = await provider.GetLatestCompatibleVersionAsync("AABBCCDD", "1.21.4", "fabric")
                         ?? throw new Exception("Expected a compatible Modrinth version.");
            Assert(latest.VersionId == "NEWVER01", "Latest compatible Modrinth version was not selected.");
            Assert(latest.SelectPrimaryFile().FileName == "sodium.jar", "Primary Modrinth file was not selected.");

            var paths = new NexoPathService(root);
            var mods = new InstanceModService(paths);
            var installed = await provider.InstallAsync(
                "fabric-test",
                projects[0],
                latest,
                null,
                mods);
            Assert(installed.InstalledMod.FileName == "sodium.jar", "Modrinth file was not installed.");
            var installedPath = Path.Combine(mods.GetModsDirectory("fabric-test"), "sodium.jar");
            Assert(File.Exists(installedPath), "Installed Modrinth JAR is missing.");
            Assert((await File.ReadAllBytesAsync(installedPath)).SequenceEqual(payload),
                "Installed Modrinth JAR content changed.");

            var matches = await provider.MatchInstalledAsync(
                mods.GetModsDirectory("fabric-test"),
                mods.List("fabric-test"));
            Assert(matches.TryGetValue("AABBCCDD", out var match), "Installed Modrinth hash was not matched.");
            Assert(match.VersionId == "OLDVER01", "Installed Modrinth version metadata was not returned.");
            Assert(!match.IsCurrent(latest), "Older installed Modrinth version should report an update.");

            mods.SetEnabled("fabric-test", "sodium.jar", enabled: false);
            var disabledMatches = await provider.MatchInstalledAsync(
                mods.GetModsDirectory("fabric-test"),
                mods.List("fabric-test"));
            var disabledMatch = disabledMatches["AABBCCDD"];
            Assert(!disabledMatch.IsEnabled, "Disabled Modrinth JAR state should be retained during hash matching.");

            var updated = await provider.InstallAsync(
                "fabric-test",
                projects[0],
                latest,
                disabledMatch,
                mods);
            Assert(!updated.InstalledMod.IsEnabled,
                "Explicit Modrinth update should preserve the prior disabled state.");

            handler.BadDownload = true;
            try
            {
                await provider.InstallAsync("fabric-bad", projects[0], latest, null, mods);
                throw new Exception("Checksum mismatch should reject a Modrinth download.");
            }
            catch (InvalidDataException ex)
            {
                Assert(ex.Message.Contains("SHA-1", StringComparison.OrdinalIgnoreCase),
                    "Checksum rejection should be actionable.");
            }
            Assert(mods.List("fabric-bad").Count == 0,
                "Failed Modrinth verification must not publish a JAR.");

            handler.BadDownload = false;
            handler.BadSearchShape = true;
            try
            {
                await provider.SearchAsync("bad-shape", "1.21.4", "fabric");
                throw new Exception("Wrong-shaped Modrinth search metadata should fail.");
            }
            catch (InvalidDataException)
            {
            }
            handler.BadSearchShape = false;

            handler.FailSearch = true;
            try
            {
                await provider.SearchAsync("network-fail", "1.21.4", "fabric");
                throw new Exception("Modrinth HTTP failure should propagate.");
            }
            catch (HttpRequestException)
            {
            }

            var unsafeHandler = new ModrinthHandler(payload) { UnsafeOnlyVersion = true };
            using var unsafeClient = new HttpClient(unsafeHandler);
            var unsafeProvider = new ModrinthModProvider(unsafeClient);
            var unsafeVersion = await unsafeProvider.GetLatestCompatibleVersionAsync(
                "AABBCCDD",
                "1.21.4",
                "fabric");
            Assert(unsafeVersion is null,
                "Traversal-shaped Modrinth filenames must never become installable provider files.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class ModrinthHandler(byte[] downloadBytes) : HttpMessageHandler
    {
        private readonly string _sha1 = Convert.ToHexString(SHA1.HashData(downloadBytes)).ToLowerInvariant();

        public bool BadDownload { get; set; }
        public bool BadSearchShape { get; set; }
        public bool FailSearch { get; set; }
        public bool UnsafeOnlyVersion { get; set; }
        public string LastUserAgent { get; private set; } = string.Empty;
        public string LastSearchQuery { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastUserAgent = request.Headers.UserAgent.ToString();
            var uri = request.RequestUri ?? throw new InvalidOperationException("Missing request URI.");

            if (uri.Host.Equals("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = BadDownload
                    ? Enumerable.Repeat((byte)9, downloadBytes.Length).ToArray()
                    : downloadBytes;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(bytes)
                };
            }

            if (uri.AbsolutePath.EndsWith("/v2/search", StringComparison.Ordinal))
            {
                LastSearchQuery = uri.Query;
                if (FailSearch)
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                if (BadSearchShape)
                    return Json("[]");

                return Json(
                    """
                    {
                      "hits": [
                        123,
                        {
                          "project_id": "AABBCCDD",
                          "slug": "sodium",
                          "title": "Sodium",
                          "description": "Rendering optimization mod",
                          "author": "CaffeineMC",
                          "downloads": 1234567,
                          "icon_url": "https://cdn.modrinth.com/data/AABBCCDD/icon.png"
                        },
                        {"project_id": 123, "slug": "broken", "title": "Broken"}
                      ]
                    }
                    """);
            }

            if (uri.AbsolutePath.EndsWith("/v2/version_files", StringComparison.Ordinal))
            {
                var body = await (request.Content?.ReadAsStringAsync(cancellationToken)
                                  ?? Task.FromResult("{}"));
                using var requestJson = JsonDocument.Parse(body);
                var hash = requestJson.RootElement.GetProperty("hashes")[0].GetString()
                           ?? throw new InvalidOperationException("Missing hash.");
                return Json(
                    $$"""
                    {
                      "{{hash}}": {
                        "id": "OLDVER01",
                        "project_id": "AABBCCDD",
                        "version_number": "0.5.0"
                      }
                    }
                    """);
            }

            if (uri.AbsolutePath.Contains("/v2/project/AABBCCDD/version", StringComparison.Ordinal))
            {
                if (UnsafeOnlyVersion)
                {
                    return Json(
                        $$"""
                        [
                          {
                            "id": "BADVER01",
                            "project_id": "AABBCCDD",
                            "name": "Unsafe",
                            "version_number": "9.9.9",
                            "date_published": "2026-09-18T00:00:00Z",
                            "files": [
                              {
                                "filename": "../escape.jar",
                                "url": "https://cdn.modrinth.com/data/AABBCCDD/versions/BADVER01/escape.jar",
                                "size": {{downloadBytes.Length}},
                                "primary": true,
                                "hashes": {"sha1": "{{_sha1}}"}
                              }
                            ]
                          }
                        ]
                        """);
                }

                return Json(
                    $$"""
                    [
                      {"id": 5},
                      {
                        "id": "OLDVER01",
                        "project_id": "AABBCCDD",
                        "name": "Old",
                        "version_number": "0.5.0",
                        "date_published": "2026-01-01T00:00:00Z",
                        "files": [
                          {
                            "filename": "../escape.jar",
                            "url": "https://cdn.modrinth.com/data/AABBCCDD/versions/OLDVER01/escape.jar",
                            "size": {{downloadBytes.Length}},
                            "primary": true,
                            "hashes": {"sha1": "{{_sha1}}"}
                          }
                        ]
                      },
                      {
                        "id": "NEWVER01",
                        "project_id": "AABBCCDD",
                        "name": "Current",
                        "version_number": "0.6.0",
                        "date_published": "2026-09-18T00:00:00Z",
                        "files": [
                          {
                            "filename": "sodium-sources.jar",
                            "url": "https://cdn.modrinth.com/data/AABBCCDD/versions/NEWVER01/sodium-sources.jar",
                            "size": {{downloadBytes.Length}},
                            "primary": false,
                            "file_type": "sources-jar",
                            "hashes": {"sha1": "{{_sha1}}"}
                          },
                          {
                            "filename": "sodium.jar",
                            "url": "https://cdn.modrinth.com/data/AABBCCDD/versions/NEWVER01/sodium.jar",
                            "size": {{downloadBytes.Length}},
                            "primary": true,
                            "hashes": {"sha1": "{{_sha1}}"}
                          }
                        ]
                      }
                    ]
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string json)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
    }
}
