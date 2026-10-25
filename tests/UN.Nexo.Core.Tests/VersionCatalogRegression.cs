using System.Net;
using System.Text;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class VersionCatalogRegression
{
    private const string ValidCatalog =
        "{\"latest\":{\"release\":\"1.21.4\",\"snapshot\":\"25w01a\"},"
        + "\"versions\":[{\"id\":\"1.21.4\",\"type\":\"release\","
        + "\"url\":\"https://metadata.example.test/1.21.4.json\","
        + "\"releaseTime\":\"2026-01-01T00:00:00Z\","
        + "\"time\":\"2026-01-01T00:00:00Z\","
        + "\"sha1\":\"0123456789abcdef0123456789abcdef01234567\",\"complianceLevel\":1}]}";

    internal static async Task RunAsync()
    {
        await TestStalledMirrorFallbackAsync();
        await TestCallerCancellationAsync();
        await TestMalformedCandidateFallbackAsync();
        await TestAllMalformedPreservesInvalidDataCauseAsync();
    }

    private static async Task TestStalledMirrorFallbackAsync()
    {
        var handler = new CatalogHandler(
            mirrorContentFactory: () => new StreamContent(new BlockingStream()),
            officialJson: ValidCatalog);
        using var client = new HttpClient(handler);
        var sources = new DownloadSourceService();
        sources.SetSource("bmclapi");
        var service = new MinecraftVersionManifestService(
            client,
            sources,
            TimeSpan.FromMilliseconds(50));
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var catalog = await service.GetCatalogAsync(guard.Token);

        Equal("1.21.4", catalog.Latest.LatestRelease,
            "stalled mirror should fall back to official catalog");
        Equal(2, handler.Hosts.Count,
            "stalled mirror should be followed by official fallback");
        Equal(false, guard.IsCancellationRequested,
            "internal body idle timeout should fire before the outer guard");
    }

    private static async Task TestCallerCancellationAsync()
    {
        var handler = new CatalogHandler(
            mirrorContentFactory: () => new StreamContent(new BlockingStream()),
            officialJson: ValidCatalog);
        using var client = new HttpClient(handler);
        var sources = new DownloadSourceService();
        sources.SetSource("bmclapi");
        var service = new MinecraftVersionManifestService(
            client,
            sources,
            TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(50));

        try
        {
            await service.GetCatalogAsync(cancellation.Token);
            throw new InvalidOperationException(
                "Explicit catalog cancellation unexpectedly succeeded.");
        }
        catch (OperationCanceledException)
        {
        }

        Equal(1, handler.Hosts.Count,
            "caller cancellation must not trigger fallback to another source");
    }

    private static async Task TestMalformedCandidateFallbackAsync()
    {
        var malformed = new[]
        {
            "{\"latest\":[],\"versions\":[]}",
            "{\"latest\":{\"release\":123,\"snapshot\":\"x\"},\"versions\":[]}",
            "{\"latest\":{\"release\":\"x\",\"snapshot\":\"y\"},\"versions\":[null]}",
            "{\"latest\":{\"release\":\"x\",\"snapshot\":\"y\"},\"versions\":[{\"id\":123,\"type\":\"release\",\"url\":\"https://example.test/v.json\",\"releaseTime\":\"2026-01-01T00:00:00Z\",\"time\":\"2026-01-01T00:00:00Z\"}]}",
            "{\"latest\":{\"release\":\"x\",\"snapshot\":\"y\"},\"versions\":[{\"id\":\"v\",\"type\":\"release\",\"url\":\"not-a-url\",\"releaseTime\":\"2026-01-01T00:00:00Z\",\"time\":\"2026-01-01T00:00:00Z\"}]}",
            "{\"latest\":{\"release\":\"x\",\"snapshot\":\"y\"},\"versions\":[{\"id\":\"v\",\"type\":\"release\",\"url\":\"https://example.test/v.json\",\"releaseTime\":\"not-a-date\",\"time\":\"2026-01-01T00:00:00Z\"}]}",
            "{\"latest\":{\"release\":\"x\",\"snapshot\":\"y\"},\"versions\":[{\"id\":\"v\",\"type\":\"release\",\"url\":\"https://example.test/v.json\",\"releaseTime\":\"2026-01-01T00:00:00Z\",\"time\":{},\"complianceLevel\":1}]}",
            "{\"latest\":{\"release\":\"x\",\"snapshot\":\"y\"},\"versions\":[{\"id\":\"v\",\"type\":\"release\",\"url\":\"https://example.test/v.json\",\"releaseTime\":\"2026-01-01T00:00:00Z\",\"time\":\"2026-01-01T00:00:00Z\",\"complianceLevel\":\"1\"}]}",
            "{\"latest\":{\"release\":\"x\",\"snapshot\":\"y\"},\"versions\":[{\"id\":\"v\",\"type\":\"release\",\"url\":\"https://example.test/v.json\",\"releaseTime\":\"2026-01-01T00:00:00Z\",\"time\":\"2026-01-01T00:00:00Z\",\"complianceLevel\":999999999999999999999}]}",
            "{\"latest\":{\"release\":\"x\",\"snapshot\":\"y\"},\"versions\":[{\"id\":\"v\",\"type\":\"release\",\"url\":\"https://example.test/v.json\",\"releaseTime\":\"2026-01-01T00:00:00Z\",\"time\":\"2026-01-01T00:00:00Z\"}]}",
            "{\"latest\":{\"release\":\"x\",\"snapshot\":\"y\"},\"versions\":[{\"id\":\"v\",\"type\":\"release\",\"url\":\"https://example.test/v.json\",\"releaseTime\":\"2026-01-01T00:00:00Z\",\"time\":\"2026-01-01T00:00:00Z\",\"sha1\":\"\"}]}",
            "{\"latest\":{\"release\":\"x\",\"snapshot\":\"y\"},\"versions\":[{\"id\":\"v\",\"type\":\"release\",\"url\":\"https://example.test/v.json\",\"releaseTime\":\"2026-01-01T00:00:00Z\",\"time\":\"2026-01-01T00:00:00Z\",\"sha1\":\"abcd\"}]}",
            "{\"latest\":{\"release\":\"x\",\"snapshot\":\"y\"},\"versions\":[{\"id\":\"v\",\"type\":\"release\",\"url\":\"https://example.test/v.json\",\"releaseTime\":\"2026-01-01T00:00:00Z\",\"time\":\"2026-01-01T00:00:00Z\",\"sha1\":\"zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz\"}]}",
            "{\"latest\":{\"release\":\"x\",\"snapshot\":\"y\"},\"versions\":[{\"id\":\"v\",\"type\":\"release\",\"url\":\"https://example.test/v.json\",\"releaseTime\":\"2026-01-01T00:00:00Z\",\"time\":\"2026-01-01T00:00:00Z\",\"sha1\":\"0123456789abcdef0123456789abcdef012345678\"}]}"
        };

        foreach (var invalid in malformed)
        {
            var handler = new CatalogHandler(
                mirrorContentFactory: () => JsonContent(invalid),
                officialJson: ValidCatalog);
            using var client = new HttpClient(handler);
            var sources = new DownloadSourceService();
            sources.SetSource("bmclapi");

            var catalog = await new MinecraftVersionManifestService(
                client,
                sources,
                TimeSpan.FromSeconds(1)).GetCatalogAsync();

            Equal("1.21.4", catalog.Latest.LatestRelease,
                "malformed mirror catalog should fall back to healthy official catalog");
            Equal(2, handler.Hosts.Count,
                "malformed mirror catalog should attempt the official fallback");
        }
    }

    private static async Task TestAllMalformedPreservesInvalidDataCauseAsync()
    {
        const string invalid =
            "{\"latest\":{\"release\":123,\"snapshot\":\"x\"},\"versions\":[]}";
        using var client = new HttpClient(
            new CatalogHandler(() => JsonContent(invalid), invalid));
        var service = new MinecraftVersionManifestService(
            client,
            new DownloadSourceService(),
            TimeSpan.FromSeconds(1));

        try
        {
            await service.GetCatalogAsync();
            throw new InvalidOperationException(
                "All-malformed catalog request unexpectedly succeeded.");
        }
        catch (HttpRequestException ex)
        {
            if (ex.InnerException is not InvalidDataException)
                throw new InvalidOperationException(
                    "Final catalog failure should retain InvalidDataException as its cause.",
                    ex);
        }
    }

    private sealed class CatalogHandler(
        Func<HttpContent> mirrorContentFactory,
        string officialJson) : HttpMessageHandler
    {
        public List<string> Hosts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var host = request.RequestUri?.Host ?? string.Empty;
            Hosts.Add(host);
            var content = host.Contains("bmclapi", StringComparison.OrdinalIgnoreCase)
                ? mirrorContentFactory()
                : JsonContent(officialJson);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
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

    private static HttpContent JsonContent(string json)
        => new StringContent(json, Encoding.UTF8, "application/json");

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(
                $"{message}: expected '{expected}', actual '{actual}'.");
    }
}
