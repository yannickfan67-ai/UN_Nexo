using System.Net;
using System.Security.Cryptography;
using System.Text;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class VanillaPathContainmentRegression
{
    internal static async Task RunAsync()
    {
        await TestUnsafeVersionIdsRejectedBeforeSideEffectsAsync();
        await TestUnsafeLibraryPathsRejectedBeforeArtifactRequestAsync();
        await TestSafeNestedLibraryPathStillWorksAsync();
    }

    private static async Task TestUnsafeVersionIdsRejectedBeforeSideEffectsAsync()
    {
        var unsafeIds = new[]
        {
            "../escaped",
            "../../escaped",
            "a/b",
            @"a\b",
            ".",
            "..",
            "/rooted",
            @"C:\escaped",
            "name:stream"
        };

        foreach (var versionId in unsafeIds)
        {
            var root = NewRoot("version-id");
            try
            {
                var paths = new NexoPathService(root);
                var handler = new PathFixtureHandler(
                    PathFixtureMode.EmptyVersion,
                    "com/example/lib/1.0/lib-1.0.jar");
                using var client = new HttpClient(handler);
                var installer = new MinecraftVanillaInstallService(
                    client,
                    paths,
                    new DownloadSourceService(),
                    TimeSpan.FromSeconds(2));
                var instance = new GameInstance(
                    Guid.NewGuid().ToString("N"),
                    "Unsafe version id",
                    versionId,
                    "vanilla",
                    DateTimeOffset.UtcNow);
                var version = Version(versionId);

                try
                {
                    await installer.InstallAsync(instance, version);
                    throw new Exception($"Unsafe version id '{versionId}' should be rejected.");
                }
                catch (InvalidDataException ex)
                {
                    Assert(
                        ex.Message.Contains("version.id", StringComparison.OrdinalIgnoreCase),
                        "Unsafe catalog version ID should identify version.id in the failure.");
                }

                Assert(handler.RequestCount == 0,
                    $"Unsafe version id '{versionId}' must fail before any HTTP request.");

                var gameRoot = paths.GetInstanceGameDirectory(instance.Id);
                Assert(!Directory.Exists(gameRoot),
                    $"Unsafe version id '{versionId}' must fail before creating the instance game tree.");

                var escaped = Path.GetFullPath(Path.Combine(root, "escaped"));
                Assert(!File.Exists(escaped) && !Directory.Exists(escaped),
                    $"Unsafe version id '{versionId}' must not create an escaped path.");
            }
            finally
            {
                TryDelete(root);
            }
        }
    }

    private static async Task TestUnsafeLibraryPathsRejectedBeforeArtifactRequestAsync()
    {
        var unsafePaths = new[]
        {
            "../escaped.jar",
            "../../escaped.jar",
            "/rooted.jar",
            @"a\b.jar",
            "a/../escaped.jar",
            "a/./escaped.jar",
            "a//escaped.jar",
            "C:/escaped.jar",
            "name:stream.jar"
        };

        foreach (var artifactPath in unsafePaths)
        {
            var root = NewRoot("library-path");
            try
            {
                var paths = new NexoPathService(root);
                var handler = new PathFixtureHandler(
                    PathFixtureMode.Library,
                    artifactPath);
                using var client = new HttpClient(handler);
                var installer = new MinecraftVanillaInstallService(
                    client,
                    paths,
                    new DownloadSourceService(),
                    TimeSpan.FromSeconds(2));
                var instance = new GameInstance(
                    Guid.NewGuid().ToString("N"),
                    "Unsafe library path",
                    "1.21.4",
                    "vanilla",
                    DateTimeOffset.UtcNow);

                try
                {
                    await installer.InstallAsync(instance, Version("1.21.4"));
                    throw new Exception($"Unsafe library path '{artifactPath}' should be rejected.");
                }
                catch (InvalidDataException ex)
                {
                    Assert(
                        ex.Message.Contains("library artifact path", StringComparison.OrdinalIgnoreCase),
                        "Unsafe library metadata should identify the artifact path.");
                }

                Assert(handler.MetadataRequests == 1,
                    $"Unsafe library path '{artifactPath}' should read only version metadata.");
                Assert(handler.ArtifactRequests == 0,
                    $"Unsafe library path '{artifactPath}' must be rejected before artifact download.");

                var gameRoot = paths.GetInstanceGameDirectory(instance.Id);
                var outside = Path.Combine(gameRoot, "escaped.jar");
                Assert(!File.Exists(outside) && !File.Exists(outside + ".part"),
                    $"Unsafe library path '{artifactPath}' must not publish an escaped file.");
                Assert(!File.Exists(Path.Combine(paths.GetInstanceDirectory(instance.Id), "install-state.json")),
                    "Rejected library path must not publish prepared state.");
            }
            finally
            {
                TryDelete(root);
            }
        }
    }

    private static async Task TestSafeNestedLibraryPathStillWorksAsync()
    {
        const string relative = "com/example/lib/1.0/lib-1.0.jar";
        var root = NewRoot("library-valid");
        try
        {
            var paths = new NexoPathService(root);
            var handler = new PathFixtureHandler(
                PathFixtureMode.Library,
                relative);
            using var client = new HttpClient(handler);
            var installer = new MinecraftVanillaInstallService(
                client,
                paths,
                new DownloadSourceService(),
                TimeSpan.FromSeconds(2));
            var instance = new GameInstance(
                Guid.NewGuid().ToString("N"),
                "Safe library path",
                "1.21.4",
                "vanilla",
                DateTimeOffset.UtcNow);

            await installer.InstallAsync(instance, Version("1.21.4"));

            var library = Path.Combine(
                paths.GetInstanceGameDirectory(instance.Id),
                "libraries",
                relative.Replace('/', Path.DirectorySeparatorChar));
            Assert(File.Exists(library),
                "Safe nested library metadata path should still publish below libraries/.");
            Assert(handler.ArtifactRequests == 1,
                "Safe nested library path should request exactly one artifact.");
            Assert(File.Exists(Path.Combine(paths.GetInstanceDirectory(instance.Id), "install-state.json")),
                "Successful safe preparation should publish prepared state.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static MinecraftVersionInfo Version(string id)
        => new(
            id,
            "release",
            "https://piston-meta.mojang.com/path-test.json",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            string.Empty,
            0);

    private static string NewRoot(string suffix)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-vanilla-path-tests",
            suffix + "-" + Guid.NewGuid().ToString("N"));
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

    private enum PathFixtureMode
    {
        EmptyVersion,
        Library
    }

    private sealed class PathFixtureHandler(
        PathFixtureMode mode,
        string artifactPath) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public int MetadataRequests { get; private set; }
        public int ArtifactRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            var uri = request.RequestUri
                ?? throw new InvalidOperationException("Missing request URI.");

            if (uri.Host.Equals("piston-meta.mojang.com", StringComparison.OrdinalIgnoreCase))
            {
                MetadataRequests++;
                var json = mode == PathFixtureMode.EmptyVersion
                    ? "{\"id\":\"1.21.4\",\"libraries\":[]}"
                    : "{\"id\":\"1.21.4\","
                      + "\"downloads\":{\"client\":{\"url\":\"https://piston-data.mojang.com/client.jar\",\"size\":6}},"
                      + "\"assetIndex\":{\"id\":\"path-assets\",\"url\":\"https://launchermeta.mojang.com/path-assets.json\"},"
                      + "\"libraries\":[{\"name\":\"com.example:lib:1.0\","
                      + "\"downloads\":{\"artifact\":{\"path\":"
                      + System.Text.Json.JsonSerializer.Serialize(artifactPath)
                      + ",\"url\":\"https://libraries.minecraft.net/path-test.jar\"}}}]}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }

            if (uri.Host.Equals("piston-data.mojang.com", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes("client"))
                });
            }

            if (uri.Host.Equals("launchermeta.mojang.com", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"objects\":{}}", Encoding.UTF8, "application/json")
                });
            }

            if (uri.Host.Equals("libraries.minecraft.net", StringComparison.OrdinalIgnoreCase))
            {
                var libraryBytes = Encoding.UTF8.GetBytes("library");
                if (uri.AbsolutePath.EndsWith(".sha1", StringComparison.Ordinal))
                {
                    var sha1 = Convert.ToHexString(
                            SHA1.HashData(libraryBytes))
                        .ToLowerInvariant();
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            sha1,
                            Encoding.ASCII,
                            "text/plain")
                    });
                }

                ArtifactRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(libraryBytes)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
