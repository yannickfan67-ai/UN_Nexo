using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UN.Nexo.Core.Models;
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

            var recommendations = await provider.RecommendAsync(
                "1.21.4",
                "fabric",
                20);
            Assert(recommendations.Count == 1,
                "Expected one compatible Modrinth recommendation.");
            Assert(recommendations[0].Project.ProjectId == "AABBCCDD",
                "Modrinth recommendation should preserve the project identity.");
            Assert(recommendations[0].Signal.Contains(
                    "Top downloads",
                    StringComparison.OrdinalIgnoreCase),
                "Modrinth recommendation signal should explain its ranking.");
            Assert(handler.LastSearchQuery.Contains(
                    "index=downloads",
                    StringComparison.OrdinalIgnoreCase),
                "Modrinth recommendations must request downloads ranking.");
            Assert(handler.LastSearchQuery.Contains(
                    "1.21.4",
                    StringComparison.Ordinal),
                "Modrinth recommendations must retain the Minecraft version facet.");
            Assert(handler.LastSearchQuery.Contains(
                    "fabric",
                    StringComparison.OrdinalIgnoreCase),
                "Modrinth recommendations must retain the loader facet.");
            Assert(!handler.LastSearchQuery.Contains(
                    "query=",
                    StringComparison.OrdinalIgnoreCase),
                "Modrinth recommendations should browse ranked compatible projects rather than inventing a search query.");

            var latest = await provider.GetLatestCompatibleVersionAsync("AABBCCDD", "1.21.4", "fabric")
                         ?? throw new Exception("Expected a compatible Modrinth version.");
            Assert(latest.VersionId == "NEWVER01", "Latest compatible Modrinth version was not selected.");
            Assert(latest.SelectPrimaryFile().FileName == "sodium.jar", "Primary Modrinth file was not selected.");
            Assert(
                latest.Dependencies.Count == 1
                && latest.Dependencies[0].ProjectId == "DEP00001"
                && latest.Dependencies[0].Type == ModProviderDependencyType.Required,
                "Required Modrinth dependency metadata was not parsed.");

            var dependencyProject = await provider.GetProjectAsync("DEP00001")
                ?? throw new Exception("Expected Modrinth dependency project metadata.");
            Assert(
                dependencyProject.Title == "Fabric API",
                "Modrinth dependency project lookup returned the wrong project.");

            var dependencyVersion = await provider.GetCompatibleVersionAsync(
                null,
                "DEPVER01",
                "1.21.4",
                "fabric")
                ?? throw new Exception("Expected a compatible version-only Modrinth dependency.");
            Assert(
                dependencyVersion.ProjectId == "DEP00001",
                "Version-only dependency lookup must resolve its owning project.");

            var paths = new NexoPathService(root);
            var mods = new InstanceModService(paths);
            var instanceId = Guid.NewGuid().ToString("N");
            var redirectOkId = Guid.NewGuid().ToString("N");
            var redirectBadId = Guid.NewGuid().ToString("N");
            var redirectLoopId = Guid.NewGuid().ToString("N");
            var badDownloadId = Guid.NewGuid().ToString("N");
            var installed = await provider.InstallAsync(
                instanceId,
                projects[0],
                latest,
                null,
                mods);
            Assert(installed.InstalledMod.FileName == "sodium.jar", "Modrinth file was not installed.");
            var installedPath = Path.Combine(mods.GetModsDirectory(instanceId), "sodium.jar");
            Assert(File.Exists(installedPath), "Installed Modrinth JAR is missing.");
            Assert((await File.ReadAllBytesAsync(installedPath)).SequenceEqual(payload),
                "Installed Modrinth JAR content changed.");

            handler.RedirectDownloadTarget =
                "https://cdn.modrinth.com/data/AABBCCDD/versions/NEWVER01/redirected.jar";
            var redirected = await provider.InstallAsync(
                redirectOkId,
                projects[0],
                latest,
                null,
                mods);
            Assert(redirected.InstalledMod.FileName == "sodium.jar",
                "Trusted same-CDN redirect should remain installable.");
            Assert(handler.RedirectedDownloadRequests > 0,
                "Trusted Modrinth redirect target should be requested after validation.");

            handler.RedirectDownloadTarget = "https://example.invalid/evil.jar";
            var untrustedRequestsBefore = handler.UntrustedDownloadRequests;
            try
            {
                await provider.InstallAsync(redirectBadId, projects[0], latest, null, mods);
                throw new Exception("Untrusted Modrinth redirect target should be rejected.");
            }
            catch (InvalidDataException ex)
            {
                Assert(ex.Message.Contains("trusted", StringComparison.OrdinalIgnoreCase)
                       || ex.Message.Contains("redirect", StringComparison.OrdinalIgnoreCase),
                    "Redirect rejection should explain the trust boundary.");
            }
            Assert(handler.UntrustedDownloadRequests == untrustedRequestsBefore,
                "Nexo must reject an untrusted redirect before sending the redirected GET.");
            Assert(mods.List(redirectBadId).Count == 0,
                "Rejected Modrinth redirect must not publish a JAR.");

            handler.RedirectDownloadTarget = null;
            handler.RedirectLoop = true;
            try
            {
                await provider.InstallAsync(redirectLoopId, projects[0], latest, null, mods);
                throw new Exception("Redirect loop should exceed the bounded redirect policy.");
            }
            catch (InvalidDataException ex)
            {
                Assert(ex.Message.Contains("redirect", StringComparison.OrdinalIgnoreCase),
                    "Redirect-loop rejection should be actionable.");
            }
            handler.RedirectLoop = false;
            Assert(mods.List(redirectLoopId).Count == 0,
                "Redirect-loop failure must not publish a JAR.");

            var matches = await provider.MatchInstalledAsync(
                mods.GetModsDirectory(instanceId),
                mods.List(instanceId));
            Assert(matches.TryGetValue("AABBCCDD", out var match), "Installed Modrinth hash was not matched.");
            Assert(match.VersionId == "OLDVER01", "Installed Modrinth version metadata was not returned.");
            Assert(!match.IsCurrent(latest), "Older installed Modrinth version should report an update.");

            var linkedInstanceId = Guid.NewGuid().ToString("N");
            var linkedModsDirectory = mods.GetModsDirectory(linkedInstanceId);
            Directory.CreateDirectory(linkedModsDirectory);
            var outsideDirectory = Path.Combine(root, "outside-linked-mod");
            Directory.CreateDirectory(outsideDirectory);
            var outsidePath = Path.Combine(outsideDirectory, "outside.bin");
            var outsideBytes = Encoding.UTF8.GetBytes(
                "outside-modrinth-fingerprint-must-not-be-sent");
            await File.WriteAllBytesAsync(outsidePath, outsideBytes);
            var linkedPath = Path.Combine(linkedModsDirectory, "external.jar");
            var linkedSupported = true;
            try
            {
                File.CreateSymbolicLink(linkedPath, outsidePath);
            }
            catch (Exception ex) when (
                ex is UnauthorizedAccessException
                or PlatformNotSupportedException
                or IOException)
            {
                linkedSupported = false;
                Console.WriteLine(
                    "SKIP Modrinth linked-JAR fixture: "
                    + ex.GetType().Name);
            }

            if (linkedSupported)
            {
                Assert(
                    mods.List(linkedInstanceId).All(item =>
                        !string.Equals(
                            item.FileName,
                            "external.jar",
                            StringComparison.Ordinal)),
                    "Instance mod listing must exclude linked JAR entries.");

                var versionFileRequestsBefore = handler.VersionFileRequests;
                var outsideHash = Convert.ToHexString(
                        SHA1.HashData(outsideBytes))
                    .ToLowerInvariant();
                var linkedMatches = await provider.MatchInstalledAsync(
                    linkedModsDirectory,
                    [
                        new InstalledMod(
                            "external.jar",
                            true,
                            outsideBytes.LongLength,
                            DateTimeOffset.UtcNow)
                    ]);

                Assert(linkedMatches.Count == 0,
                    "Linked installed JAR must not produce a Modrinth match.");
                Assert(handler.VersionFileRequests == versionFileRequestsBefore,
                    "A linked-only installed set must not trigger version_files.");
                Assert(!handler.LastVersionFileHashes.Contains(
                        outsideHash,
                        StringComparer.OrdinalIgnoreCase),
                    "The linked target SHA-1 must never be sent to Modrinth.");
            }

            mods.SetEnabled(instanceId, "sodium.jar", enabled: false);
            var disabledMatches = await provider.MatchInstalledAsync(
                mods.GetModsDirectory(instanceId),
                mods.List(instanceId));
            var disabledMatch = disabledMatches["AABBCCDD"];
            Assert(!disabledMatch.IsEnabled, "Disabled Modrinth JAR state should be retained during hash matching.");

            var updated = await provider.InstallAsync(
                instanceId,
                projects[0],
                latest,
                disabledMatch,
                mods);
            Assert(!updated.InstalledMod.IsEnabled,
                "Explicit Modrinth update should preserve the prior disabled state.");

            var renamedVersion = new ModProviderVersion(
                "modrinth",
                projects[0].ProjectId,
                "RENAME01",
                "Renamed current",
                "0.7.0",
                DateTimeOffset.UtcNow,
                [
                    new ModProviderFile(
                        "sodium-next.jar",
                        "https://cdn.modrinth.com/data/AABBCCDD/versions/RENAME01/sodium-next.jar",
                        Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant(),
                        payload.LongLength,
                        true)
                ]);

            var atomicInstanceId = Guid.NewGuid().ToString("N");
            var legacySource = Path.Combine(root, "legacy-sodium.jar");
            await File.WriteAllBytesAsync(legacySource, Encoding.UTF8.GetBytes("legacy"));
            var legacyInstalled = await mods.InstallAsync(
                atomicInstanceId,
                legacySource);
            var legacyDisabled = await mods.SetEnabledAsync(
                atomicInstanceId,
                legacyInstalled.FileName,
                enabled: false);
            var atomicExisting = new ModProviderInstalledMatch(
                "modrinth",
                projects[0].ProjectId,
                "OLDVER01",
                "0.5.0",
                legacyDisabled.FileName,
                false);

            var atomicModsDirectory = mods.GetModsDirectory(atomicInstanceId);
            var oldMutationKey = Path.Combine(
                atomicModsDirectory,
                "legacy-sodium.jar");
            var newDisabledPath = Path.Combine(
                atomicModsDirectory,
                "sodium-next.jar.disabled");
            var newEnabledPath = Path.Combine(
                atomicModsDirectory,
                "sodium-next.jar");
            var oldDisabledPath = Path.Combine(
                atomicModsDirectory,
                legacyDisabled.FileName);

            using var oldPathLease = await PathKeyedLock.AcquireAsync(
                oldMutationKey);
            using var atomicTimeout =
                new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var atomicUpdate = provider.InstallAsync(
                atomicInstanceId,
                projects[0],
                renamedVersion,
                atomicExisting,
                mods,
                atomicTimeout.Token);

            await WaitForFileAsync(
                newDisabledPath,
                atomicTimeout.Token);
            Assert(!File.Exists(newEnabledPath),
                "Updating a disabled mod must publish the new version directly as disabled.");

            var competingCoordinator =
                new InstanceOperationCoordinator(paths);
            var competingLeaseTask = competingCoordinator
                .AcquireAsync(
                    atomicInstanceId,
                    "play-test",
                    atomicTimeout.Token)
                .AsTask();
            await Task.Delay(150, atomicTimeout.Token);
            Assert(!competingLeaseTask.IsCompleted,
                "Play-style instance acquisition must wait until the complete Modrinth update finishes.");

            oldPathLease.Dispose();
            var atomicResult = await atomicUpdate;
            await using (var competingLease =
                         await competingLeaseTask.WaitAsync(
                             atomicTimeout.Token))
            {
                Assert(!File.Exists(oldDisabledPath),
                    "Filename-changing update must remove the previous JAR before releasing the instance lease.");
                Assert(File.Exists(newDisabledPath),
                    "Filename-changing update must retain the new disabled JAR.");
                Assert(!atomicResult.InstalledMod.IsEnabled,
                    "Atomic filename-changing update must preserve disabled state.");
            }

            var cancelInstanceId = Guid.NewGuid().ToString("N");
            var cancelInstalled = await mods.InstallAsync(
                cancelInstanceId,
                legacySource);
            var cancelDisabled = await mods.SetEnabledAsync(
                cancelInstanceId,
                cancelInstalled.FileName,
                enabled: false);
            var cancelExisting = atomicExisting with
            {
                LocalFileName = cancelDisabled.FileName
            };
            var cancelModsDirectory = mods.GetModsDirectory(cancelInstanceId);
            var cancelOldKey = Path.Combine(
                cancelModsDirectory,
                "legacy-sodium.jar");
            var cancelOldPath = Path.Combine(
                cancelModsDirectory,
                cancelDisabled.FileName);
            var cancelNewPath = Path.Combine(
                cancelModsDirectory,
                "sodium-next.jar.disabled");

            using var cancelOldLease = await PathKeyedLock.AcquireAsync(
                cancelOldKey);
            using var updateCancellation =
                new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var cancelledUpdate = provider.InstallAsync(
                cancelInstanceId,
                projects[0],
                renamedVersion,
                cancelExisting,
                mods,
                updateCancellation.Token);
            await WaitForFileAsync(
                cancelNewPath,
                updateCancellation.Token);
            updateCancellation.Cancel();

            try
            {
                await cancelledUpdate;
                throw new Exception(
                    "Cancelled Modrinth update unexpectedly completed.");
            }
            catch (OperationCanceledException)
            {
            }

            Assert(File.Exists(cancelOldPath),
                "Cancelled provider update must leave the previous disabled mod recoverable.");
            Assert(!File.Exists(cancelNewPath),
                "Cancelled provider update must roll back the newly published file.");

            handler.BadDownload = true;
            try
            {
                await provider.InstallAsync(badDownloadId, projects[0], latest, null, mods);
                throw new Exception("Checksum mismatch should reject a Modrinth download.");
            }
            catch (InvalidDataException ex)
            {
                Assert(ex.Message.Contains("SHA-1", StringComparison.OrdinalIgnoreCase),
                    "Checksum rejection should be actionable.");
            }
            Assert(mods.List(badDownloadId).Count == 0,
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

    private static async Task WaitForFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        while (!File.Exists(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(25, cancellationToken);
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
        public string? RedirectDownloadTarget { get; set; }
        public bool RedirectLoop { get; set; }
        public int RedirectedDownloadRequests { get; private set; }
        public int UntrustedDownloadRequests { get; private set; }
        public int VersionFileRequests { get; private set; }
        public IReadOnlyList<string> LastVersionFileHashes { get; private set; } = [];
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
                if (RedirectLoop)
                {
                    var loop = new HttpResponseMessage(HttpStatusCode.Found);
                    loop.Headers.Location = new Uri(
                        uri.AbsolutePath.EndsWith("/redirect-loop-a", StringComparison.Ordinal)
                            ? "https://cdn.modrinth.com/redirect-loop-b"
                            : "https://cdn.modrinth.com/redirect-loop-a");
                    return loop;
                }

                if (RedirectDownloadTarget is not null
                    && uri.AbsolutePath.EndsWith("/sodium.jar", StringComparison.Ordinal))
                {
                    var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                    redirect.Headers.Location = new Uri(RedirectDownloadTarget);
                    return redirect;
                }

                if (uri.AbsolutePath.EndsWith("/redirected.jar", StringComparison.Ordinal))
                    RedirectedDownloadRequests++;

                var bytes = BadDownload
                    ? Enumerable.Repeat((byte)9, downloadBytes.Length).ToArray()
                    : downloadBytes;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(bytes)
                };
            }

            if (!uri.Host.Equals("api.modrinth.com", StringComparison.OrdinalIgnoreCase))
            {
                UntrustedDownloadRequests++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(downloadBytes)
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
                VersionFileRequests++;
                var body = await (request.Content?.ReadAsStringAsync(cancellationToken)
                                  ?? Task.FromResult("{}"));
                using var requestJson = JsonDocument.Parse(body);
                LastVersionFileHashes = requestJson.RootElement
                    .GetProperty("hashes")
                    .EnumerateArray()
                    .Select(item => item.GetString() ?? string.Empty)
                    .Where(item => item.Length > 0)
                    .ToArray();
                var hash = LastVersionFileHashes.FirstOrDefault()
                           ?? throw new InvalidOperationException("Missing hash.");
                return Json(
                    JsonSerializer.Serialize(
                        new Dictionary<string, object>
                        {
                            [hash] = new
                            {
                                id = "OLDVER01",
                                project_id = "AABBCCDD",
                                version_number = "0.5.0"
                            }
                        }));
            }

            if (uri.AbsolutePath.Equals("/v2/project/DEP00001", StringComparison.Ordinal))
            {
                return Json(
                    """
                    {
                      "id": "DEP00001",
                      "slug": "fabric-api",
                      "title": "Fabric API",
                      "description": "Core hooks for Fabric mods",
                      "downloads": 987654,
                      "icon_url": "https://cdn.modrinth.com/data/DEP00001/icon.png"
                    }
                    """);
            }

            if (uri.AbsolutePath.Contains("/v2/project/DEP00001/version", StringComparison.Ordinal))
            {
                return Json(
                    $$"""
                    [
                      {
                        "id": "DEPVER01",
                        "project_id": "DEP00001",
                        "name": "Fabric API compatible",
                        "version_number": "1.0.0",
                        "date_published": "2026-09-18T01:00:00Z",
                        "game_versions": ["1.21.4"],
                        "loaders": ["fabric"],
                        "dependencies": [],
                        "files": [
                          {
                            "filename": "fabric-api.jar",
                            "url": "https://cdn.modrinth.com/data/DEP00001/versions/DEPVER01/fabric-api.jar",
                            "size": {{downloadBytes.Length}},
                            "primary": true,
                            "hashes": {"sha1": "{{_sha1}}"}
                          }
                        ]
                      }
                    ]
                    """);
            }

            if (uri.AbsolutePath.Equals("/v2/version/DEPVER01", StringComparison.Ordinal))
            {
                return Json(
                    $$"""
                    {
                      "id": "DEPVER01",
                      "project_id": "DEP00001",
                      "name": "Fabric API compatible",
                      "version_number": "1.0.0",
                      "date_published": "2026-09-18T01:00:00Z",
                      "game_versions": ["1.21.4"],
                      "loaders": ["fabric"],
                      "dependencies": [],
                      "files": [
                        {
                          "filename": "fabric-api.jar",
                          "url": "https://cdn.modrinth.com/data/DEP00001/versions/DEPVER01/fabric-api.jar",
                          "size": {{downloadBytes.Length}},
                          "primary": true,
                          "hashes": {"sha1": "{{_sha1}}"}
                        }
                      ]
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
                        "game_versions": ["1.21.4"],
                        "loaders": ["fabric"],
                        "dependencies": [
                          {
                            "project_id": "DEP00001",
                            "version_id": null,
                            "dependency_type": "required"
                          }
                        ],
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
