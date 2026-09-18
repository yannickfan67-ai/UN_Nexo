using System.Net;
using System.Text;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class MetadataSizeRegression
{
    private const int ManifestLimit = 16 * 1024 * 1024;
    private const int VersionLimit = 8 * 1024 * 1024;
    private const int AssetIndexLimit = 64 * 1024 * 1024;

    internal static async Task RunAsync()
    {
        await TestManifestDeclaredLengthFallbackAsync();
        await TestManifestUnknownLengthLimitAsync();
        await TestManifestJustUnderLimitAsync();
        await TestVanillaVersionDeclaredLengthLimitAsync();
        await TestVanillaVersionUnknownLengthLimitAsync();
        await TestVanillaAssetIndexDeclaredLengthLimitAsync();
        await TestVanillaVersionJustUnderLimitAsync();
    }

    private static async Task TestManifestDeclaredLengthFallbackAsync()
    {
        var oversized = new DeclaredLengthContent(ManifestLimit + 1L);
        var handler = new ManifestFallbackHandler(oversized);
        using var client = new HttpClient(handler);
        var sources = new DownloadSourceService();
        sources.SetSource("bmclapi");
        var catalog = await new MinecraftVersionManifestService(client, sources)
            .GetCatalogAsync();

        Assert(catalog.Latest.LatestRelease == "1.21.4",
            "Oversized mirror catalog should fall back to the official candidate.");
        Assert(handler.Hosts.Count == 2,
            "Oversized mirror catalog should try the fallback candidate.");
        Assert(!oversized.ReadAttempted,
            "Declared oversized manifest must be rejected before reading its body.");
    }

    private static async Task TestManifestUnknownLengthLimitAsync()
    {
        var stream = new GeneratingStream(ManifestLimit + 1L);
        using var client = new HttpClient(new SingleContentHandler(new StreamContent(stream)));
        var service = new MinecraftVersionManifestService(
            client,
            new DownloadSourceService());

        try
        {
            await service.GetCatalogAsync();
            throw new Exception("Unknown-length oversized manifest should be rejected.");
        }
        catch (HttpRequestException ex)
        {
            Assert(ex.InnerException is InvalidDataException,
                "Unknown-length oversized manifest should fail as invalid bounded metadata.");
        }

        Assert(stream.BytesRead > ManifestLimit,
            "Unknown-length manifest test must cross the configured byte limit.");
    }

    private static async Task TestManifestJustUnderLimitAsync()
    {
        const string core =
            "{\"latest\":{\"release\":\"1.21.4\",\"snapshot\":\"1.21.4\"},\"versions\":[]}";
        var body = core.PadRight(ManifestLimit - 1, ' ');
        using var client = new HttpClient(new SingleContentHandler(
            new StringContent(body, Encoding.UTF8, "application/json")));
        var catalog = await new MinecraftVersionManifestService(
            client,
            new DownloadSourceService()).GetCatalogAsync();

        Assert(catalog.Latest.LatestRelease == "1.21.4",
            "Just-under-limit manifest should remain accepted.");
    }

    private static async Task TestVanillaVersionDeclaredLengthLimitAsync()
    {
        var root = NewRoot();
        try
        {
            var oversized = new DeclaredLengthContent(VersionLimit + 1L);
            using var client = new HttpClient(new SingleContentHandler(oversized));
            var paths = new NexoPathService(root);
            var service = new MinecraftVanillaInstallService(
                client,
                paths,
                new DownloadSourceService(),
                TimeSpan.FromSeconds(2));
            var instance = Instance("version-declared-limit");

            await ExpectInvalidDataAsync(
                () => service.InstallAsync(instance, Version(instance.VersionId)),
                "Declared oversized version metadata");

            Assert(!oversized.ReadAttempted,
                "Declared oversized version metadata must be rejected before body reads.");
            AssertNoPreparedOrPart(paths, instance, instance.VersionId + ".json");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task TestVanillaVersionUnknownLengthLimitAsync()
    {
        var root = NewRoot();
        try
        {
            var stream = new GeneratingStream(VersionLimit + 1L);
            using var client = new HttpClient(new SingleContentHandler(new StreamContent(stream)));
            var paths = new NexoPathService(root);
            var service = new MinecraftVanillaInstallService(
                client,
                paths,
                new DownloadSourceService(),
                TimeSpan.FromSeconds(2));
            var instance = Instance("version-stream-limit");

            await ExpectInvalidDataAsync(
                () => service.InstallAsync(instance, Version(instance.VersionId)),
                "Unknown-length oversized version metadata");

            Assert(stream.BytesRead > VersionLimit,
                "Unknown-length version metadata test must cross the byte limit.");
            AssertNoPreparedOrPart(paths, instance, instance.VersionId + ".json");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task TestVanillaAssetIndexDeclaredLengthLimitAsync()
    {
        var root = NewRoot();
        try
        {
            var oversized = new DeclaredLengthContent(AssetIndexLimit + 1L);
            using var client = new HttpClient(new AssetLimitHandler(oversized));
            var paths = new NexoPathService(root);
            var service = new MinecraftVanillaInstallService(
                client,
                paths,
                new DownloadSourceService(),
                TimeSpan.FromSeconds(2));
            var instance = Instance("asset-index-limit");

            await ExpectInvalidDataAsync(
                () => service.InstallAsync(instance, Version(instance.VersionId)),
                "Declared oversized asset index");

            Assert(!oversized.ReadAttempted,
                "Declared oversized asset index must be rejected before body reads.");
            var indexPath = Path.Combine(
                paths.GetInstanceGameDirectory(instance.Id),
                "assets",
                "indexes",
                "limit-assets.json");
            Assert(!File.Exists(indexPath),
                "Oversized asset index must not be published.");
            Assert(!File.Exists(indexPath + ".part"),
                "Oversized asset index staging file must be cleaned up.");
            Assert(!File.Exists(Path.Combine(paths.GetInstanceDirectory(instance.Id), "install-state.json")),
                "Oversized asset index must not publish prepared state.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task TestVanillaVersionJustUnderLimitAsync()
    {
        var root = NewRoot();
        try
        {
            const string core =
                "{\"id\":\"version-near-limit\",\"libraries\":[]}";
            var body = core.PadRight(VersionLimit - 1, ' ');
            using var client = new HttpClient(new SingleContentHandler(
                new StringContent(body, Encoding.UTF8, "application/json")));
            var paths = new NexoPathService(root);
            var service = new MinecraftVanillaInstallService(
                client,
                paths,
                new DownloadSourceService(),
                TimeSpan.FromSeconds(2));
            var instance = Instance("version-near-limit");

            await service.InstallAsync(instance, Version(instance.VersionId));

            Assert(File.Exists(Path.Combine(paths.GetInstanceDirectory(instance.Id), "install-state.json")),
                "Just-under-limit version metadata should still prepare successfully.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static GameInstance Instance(string id)
        => new(id, id, id, "vanilla", DateTimeOffset.UtcNow);

    private static MinecraftVersionInfo Version(string id)
        => new(
            id,
            "release",
            "https://metadata.example.test/version.json",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            string.Empty,
            0);

    private static string NewRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-metadata-size-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task ExpectInvalidDataAsync(Func<Task> action, string label)
    {
        try
        {
            await action();
            throw new Exception(label + " should be rejected.");
        }
        catch (InvalidDataException)
        {
        }
    }

    private static void AssertNoPreparedOrPart(
        NexoPathService paths,
        GameInstance instance,
        string versionFileName)
    {
        var versionPath = Path.Combine(
            paths.GetInstanceGameDirectory(instance.Id),
            "versions",
            instance.VersionId,
            versionFileName);
        Assert(!File.Exists(versionPath),
            "Oversized version metadata must not be published.");
        Assert(!File.Exists(versionPath + ".part"),
            "Oversized version metadata staging file must be cleaned up.");
        Assert(!File.Exists(Path.Combine(paths.GetInstanceDirectory(instance.Id), "install-state.json")),
            "Oversized version metadata must not publish prepared state.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
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

    private sealed class ManifestFallbackHandler(DeclaredLengthContent oversized) : HttpMessageHandler
    {
        public List<string> Hosts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var host = request.RequestUri?.Host ?? string.Empty;
            Hosts.Add(host);
            if (host.Contains("bmclapi", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(Response(oversized));

            const string manifest =
                "{\"latest\":{\"release\":\"1.21.4\",\"snapshot\":\"1.21.4\"},\"versions\":[]}";
            return Task.FromResult(Response(
                new StringContent(manifest, Encoding.UTF8, "application/json")));
        }
    }

    private sealed class AssetLimitHandler(DeclaredLengthContent oversized) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Missing request URI.");
            if (uri.Host.Equals("metadata.example.test", StringComparison.OrdinalIgnoreCase))
            {
                const string metadata =
                    "{\"id\":\"asset-index-limit\",\"libraries\":[],"
                    + "\"assetIndex\":{\"id\":\"limit-assets\","
                    + "\"url\":\"https://assets.example.test/index.json\"}}";
                return Task.FromResult(Response(
                    new StringContent(metadata, Encoding.UTF8, "application/json")));
            }

            if (uri.Host.Equals("assets.example.test", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(Response(oversized));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class SingleContentHandler(HttpContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(Response(content));
    }

    private sealed class DeclaredLengthContent(long declaredLength) : HttpContent
    {
        public bool ReadAttempted { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            ReadAttempted = true;
            throw new InvalidOperationException("Declared oversized content must not be read.");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = declaredLength;
            return true;
        }
    }

    private sealed class GeneratingStream(long length) : Stream
    {
        private long _remaining = length;
        public long BytesRead { get; private set; }

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

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_remaining <= 0)
                return ValueTask.FromResult(0);

            var count = (int)Math.Min(buffer.Length, _remaining);
            buffer.Span[..count].Fill((byte)' ');
            _remaining -= count;
            BytesRead += count;
            return ValueTask.FromResult(count);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static HttpResponseMessage Response(HttpContent content)
        => new(HttpStatusCode.OK) { Content = content };
}
