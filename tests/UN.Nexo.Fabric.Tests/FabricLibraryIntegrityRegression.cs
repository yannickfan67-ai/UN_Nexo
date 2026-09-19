using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Fabric.Tests;

internal static class FabricLibraryIntegrityRegression
{
    private const string BaseVersionId = "1.21.4";
    private const string LoaderVersion = "0.16.9";
    private const string ChildVersionId = "fabric-loader-0.16.9-1.21.4";
    private const string TestLibraryRelative = "com/example/testlib/1.0/testlib-1.0.jar";

    internal static async Task RunAsync()
    {
        await TestCorruptCachedMavenJarIsReplacedAsync();
        await TestTruncatedMavenJarIsRejectedAsync();
        await TestStalledMavenJarTimesOutAsync();
        await TestLinkedInstallStateRejectedAsync();
        await TestLinkedVersionsDirectoryRejectedAsync();
    }

    private static async Task TestCorruptCachedMavenJarIsReplacedAsync()
    {
        var root = NewRoot();
        try
        {
            var jar = CreateJarBytes("valid-library");
            var handler = new FabricLibraryHandler(jar, LibraryMode.Valid);
            var fixture = await CreateFixtureAsync(root, handler, TimeSpan.FromSeconds(1));

            var target = Path.Combine(
                fixture.Paths.GetInstanceGameDirectory(fixture.Instance.Id),
                "libraries",
                TestLibraryRelative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllBytesAsync(target, [1]);

            await fixture.Service.PrepareAsync(fixture.Instance, fixture.BaseVersion);

            var actual = await File.ReadAllBytesAsync(target);
            Equal(
                Convert.ToHexString(SHA1.HashData(jar)),
                Convert.ToHexString(SHA1.HashData(actual)),
                "corrupt cached Maven JAR should be replaced with checksum-verified bytes");
            Equal(1, handler.TestLibraryJarRequests,
                "corrupt cached Maven JAR should trigger one artifact download");
            Equal(true,
                File.Exists(Path.Combine(
                    fixture.Paths.GetInstanceDirectory(fixture.Instance.Id),
                    "install-state.json")),
                "successful verified Fabric preparation should publish state");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task TestTruncatedMavenJarIsRejectedAsync()
    {
        var root = NewRoot();
        try
        {
            var jar = CreateJarBytes("expected-library");
            var handler = new FabricLibraryHandler(jar, LibraryMode.Truncated);
            var fixture = await CreateFixtureAsync(root, handler, TimeSpan.FromSeconds(1));

            try
            {
                await fixture.Service.PrepareAsync(fixture.Instance, fixture.BaseVersion);
                throw new Exception("Truncated Fabric Maven response unexpectedly prepared.");
            }
            catch (InvalidDataException)
            {
            }

            var target = Path.Combine(
                fixture.Paths.GetInstanceGameDirectory(fixture.Instance.Id),
                "libraries",
                TestLibraryRelative.Replace('/', Path.DirectorySeparatorChar));
            Equal(false, File.Exists(target),
                "truncated Maven response must not publish the final library");
            Equal(false, File.Exists(target + ".part"),
                "truncated Maven response must clean its .part file");
            Equal(false,
                File.Exists(Path.Combine(
                    fixture.Paths.GetInstanceDirectory(fixture.Instance.Id),
                    "install-state.json")),
                "truncated Maven response must not publish prepared state");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task TestStalledMavenJarTimesOutAsync()
    {
        var root = NewRoot();
        try
        {
            var jar = CreateJarBytes("expected-library");
            var handler = new FabricLibraryHandler(jar, LibraryMode.Stall);
            var fixture = await CreateFixtureAsync(
                root,
                handler,
                TimeSpan.FromMilliseconds(50));
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(2));

            try
            {
                await fixture.Service.PrepareAsync(
                    fixture.Instance,
                    fixture.BaseVersion,
                    cancellationToken: guard.Token);
                throw new Exception("Stalled Fabric Maven response unexpectedly prepared.");
            }
            catch (TimeoutException)
            {
            }

            Equal(false, guard.IsCancellationRequested,
                "Fabric library idle timeout should fire before the outer guard");
            var target = Path.Combine(
                fixture.Paths.GetInstanceGameDirectory(fixture.Instance.Id),
                "libraries",
                TestLibraryRelative.Replace('/', Path.DirectorySeparatorChar));
            Equal(false, File.Exists(target),
                "stalled Maven response must not publish the final library");
            Equal(false, File.Exists(target + ".part"),
                "stalled Maven response must clean its .part file");
            Equal(false,
                File.Exists(Path.Combine(
                    fixture.Paths.GetInstanceDirectory(fixture.Instance.Id),
                    "install-state.json")),
                "stalled Maven response must not publish prepared state");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task TestLinkedInstallStateRejectedAsync()
    {
        var root = NewRoot();
        var externalRoot =
            Path.Combine(
                Path.GetTempPath(),
                "un-nexo-fabric-state-external-"
                + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(externalRoot);

        try
        {
            var jar = CreateJarBytes("valid-library");
            var handler =
                new FabricLibraryHandler(
                    jar,
                    LibraryMode.Valid);
            using var fixture =
                await CreateFixtureAsync(
                    root,
                    handler,
                    TimeSpan.FromSeconds(1));

            var sentinel =
                Path.Combine(
                    externalRoot,
                    "outside.json");
            var sentinelBytes =
                Encoding.UTF8.GetBytes(
                    "{\"outside\":true}");
            await File.WriteAllBytesAsync(
                sentinel,
                sentinelBytes);

            var statePath =
                Path.Combine(
                    fixture.Paths.GetInstanceDirectory(
                        fixture.Instance.Id),
                    "install-state.json");
            if (!TryCreateFileLink(
                    statePath,
                    sentinel))
            {
                Console.WriteLine(
                    "SKIP Fabric linked install-state regression: platform denied symlink creation");
                return;
            }

            try
            {
                await fixture.Service.PrepareAsync(
                    fixture.Instance,
                    fixture.BaseVersion);
                throw new Exception(
                    "Fabric unexpectedly accepted a linked install-state rollback source.");
            }
            catch (InvalidDataException)
            {
            }

            Equal(
                true,
                File.Exists(statePath),
                "Fabric rejection should leave the linked state path in place.");
            Equal(
                Convert.ToHexString(sentinelBytes),
                Convert.ToHexString(
                    await File.ReadAllBytesAsync(sentinel)),
                "Fabric must not modify the external install-state target.");
            Equal(
                0,
                handler.TestLibraryJarRequests,
                "Fabric must reject linked install state before loader library mutation.");
        }
        finally
        {
            // Recursive cleanup removes the symlink itself; the external target
            // lives outside the test root and is removed separately.
            TryDelete(root);
            TryDelete(externalRoot);
        }
    }

    private static async Task TestLinkedVersionsDirectoryRejectedAsync()
    {
        var root = NewRoot();
        var externalRoot =
            Path.Combine(
                Path.GetTempPath(),
                "un-nexo-fabric-profile-external-"
                + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(externalRoot);

        var linkedVersions = string.Empty;
        try
        {
            var jar = CreateJarBytes("valid-library");
            var handler =
                new FabricLibraryHandler(
                    jar,
                    LibraryMode.Valid);
            using var client =
                new HttpClient(handler);
            var paths =
                new NexoPathService(root);
            paths.EnsureDirectories();

            var instance =
                new GameInstance(
                    Guid.NewGuid().ToString("N"),
                    "Fabric linked versions",
                    ChildVersionId,
                    "fabric",
                    DateTimeOffset.UtcNow,
                    BaseVersionId,
                    LoaderVersion);
            var instanceRoot =
                paths.GetInstanceDirectory(instance.Id);
            Directory.CreateDirectory(instanceRoot);
            var gameRoot =
                paths.GetInstanceGameDirectory(instance.Id);
            Directory.CreateDirectory(gameRoot);
            linkedVersions =
                Path.Combine(
                    gameRoot,
                    "versions");

            if (!TryCreateDirectoryLink(
                    linkedVersions,
                    externalRoot))
            {
                Console.WriteLine(
                    "SKIP Fabric linked versions regression: platform denied symlink creation");
                return;
            }

            var vanilla =
                new MinecraftVanillaInstallService(
                    client,
                    paths,
                    new DownloadSourceService(),
                    TimeSpan.FromSeconds(1));
            var service =
                new FabricInstallService(
                    client,
                    paths,
                    vanilla,
                    new FabricMetaService(client),
                    TimeSpan.FromSeconds(1));
            var baseVersion =
                new MinecraftVersionInfo(
                    BaseVersionId,
                    "release",
                    "https://piston-meta.mojang.com/version.json",
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    string.Empty,
                    0);

            try
            {
                await service.PrepareAsync(
                    instance,
                    baseVersion);
                throw new Exception(
                    "Fabric unexpectedly accepted a linked versions directory.");
            }
            catch (InvalidDataException)
            {
            }

            Equal(
                false,
                Directory.EnumerateFileSystemEntries(
                    externalRoot)
                    .Any(),
                "Fabric profile preparation must not write through a linked versions directory.");
            Equal(
                0,
                handler.TestLibraryJarRequests,
                "Fabric must reject linked versions before loader library mutation.");
        }
        finally
        {
            TryDeleteDirectoryLink(linkedVersions);
            TryDelete(root);
            TryDelete(externalRoot);
        }
    }

    private static async Task<Fixture> CreateFixtureAsync(
        string root,
        FabricLibraryHandler handler,
        TimeSpan idleTimeout)
    {
        var paths = new NexoPathService(root);
        paths.EnsureDirectories();
        var instance = new GameInstance(
            Guid.NewGuid().ToString("N"),
            "Fabric library test",
            ChildVersionId,
            "fabric",
            DateTimeOffset.UtcNow,
            BaseVersionId,
            LoaderVersion);
        var childRoot = Path.Combine(
            paths.GetInstanceGameDirectory(instance.Id),
            "versions",
            ChildVersionId);
        Directory.CreateDirectory(childRoot);

        await File.WriteAllTextAsync(
            Path.Combine(childRoot, ChildVersionId + ".json"),
            """
            {
              "id":"fabric-loader-0.16.9-1.21.4",
              "inheritsFrom":"1.21.4",
              "libraries":[
                {
                  "name":"net.fabricmc:fabric-loader:0.16.9",
                  "url":"https://maven.fabricmc.net/"
                },
                {
                  "name":"com.example:testlib:1.0",
                  "url":"https://maven.fabricmc.net/"
                }
              ]
            }
            """);

        var client = new HttpClient(handler);
        var sources = new DownloadSourceService();
        var vanilla = new MinecraftVanillaInstallService(
            client,
            paths,
            sources,
            idleTimeout);
        var service = new FabricInstallService(
            client,
            paths,
            vanilla,
            new FabricMetaService(client),
            idleTimeout);
        var version = new MinecraftVersionInfo(
            BaseVersionId,
            "release",
            "https://piston-meta.mojang.com/version.json",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            string.Empty,
            0);

        return new Fixture(paths, instance, version, service, client);
    }

    private static byte[] CreateJarBytes(string payload)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("payload.txt", CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(payload);
        }
        return memory.ToArray();
    }

    private static bool TryCreateFileLink(
        string linkPath,
        string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(
                linkPath,
                targetPath);
            return true;
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException
            or IOException
            or PlatformNotSupportedException
            or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryCreateDirectoryLink(
        string linkPath,
        string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(
                linkPath,
                targetPath);
            return true;
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException
            or IOException
            or PlatformNotSupportedException
            or NotSupportedException)
        {
            return false;
        }
    }

    private static void TryDeleteDirectoryLink(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path);
        }
        catch
        {
        }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-fabric-library-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string root)
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

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception(
                $"{message}: expected '{expected}', actual '{actual}'.");
    }

    private enum LibraryMode
    {
        Valid,
        Truncated,
        Stall
    }

    private sealed class FabricLibraryHandler(
        byte[] expectedJar,
        LibraryMode mode) : HttpMessageHandler
    {
        private readonly string _sha1 =
            Convert.ToHexString(SHA1.HashData(expectedJar)).ToLowerInvariant();

        public int TestLibraryJarRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri
                ?? throw new InvalidOperationException("Missing request URI.");

            if (uri.AbsolutePath.EndsWith(
                    "/version.json",
                    StringComparison.OrdinalIgnoreCase))
            {
                const string metadata =
                    "{\"id\":\"1.21.4\",\"libraries\":[],"
                    + "\"downloads\":{\"client\":{\"url\":\"https://piston-data.mojang.com/client.jar\",\"size\":6}},"
                    + "\"assetIndex\":{\"id\":\"fabric-library-assets\","
                    + "\"url\":\"https://launchermeta.mojang.com/fabric-library-assets.json\"}}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        metadata,
                        Encoding.UTF8,
                        "application/json")
                });
            }

            if (uri.AbsolutePath.EndsWith(
                    "/client.jar",
                    StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(
                        Encoding.UTF8.GetBytes("client"))
                });
            }

            if (uri.AbsolutePath.EndsWith(
                    "/fabric-library-assets.json",
                    StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"objects\":{}}",
                        Encoding.UTF8,
                        "application/json")
                });
            }

            if (!uri.Host.Equals("maven.fabricmc.net", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            if (uri.AbsolutePath.EndsWith(".sha1", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_sha1, Encoding.ASCII, "text/plain")
                });
            }

            if (uri.AbsolutePath.EndsWith(
                    "/com/example/testlib/1.0/testlib-1.0.jar",
                    StringComparison.Ordinal))
            {
                TestLibraryJarRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = mode switch
                    {
                        LibraryMode.Valid => new ByteArrayContent(expectedJar),
                        LibraryMode.Truncated => new ByteArrayContent([1]),
                        LibraryMode.Stall => new StreamContent(new BlockingStream()),
                        _ => throw new ArgumentOutOfRangeException()
                    }
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(expectedJar)
            });
        }
    }

    private sealed class BlockingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();
        public override void SetLength(long value)
            => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
    }

    private sealed record Fixture(
        NexoPathService Paths,
        GameInstance Instance,
        MinecraftVersionInfo BaseVersion,
        FabricInstallService Service,
        HttpClient Client) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }
}
