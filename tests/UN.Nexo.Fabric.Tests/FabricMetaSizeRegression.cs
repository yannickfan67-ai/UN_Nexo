using System.Net;
using System.Text;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Fabric.Tests;

internal static class FabricMetaSizeRegression
{
    private const int MetadataLimit = 4 * 1024 * 1024;

    internal static async Task RunAsync()
    {
        await TestDeclaredLoaderListLimitAsync();
        await TestDeclaredProfileLimitAsync();
        await TestUnknownLengthLimitAsync();
        await TestJustUnderLimitAsync();
    }

    private static async Task TestDeclaredLoaderListLimitAsync()
    {
        var oversized = new DeclaredLengthContent(MetadataLimit + 1L);
        using var client = new HttpClient(new FixedContentHandler(oversized));
        var service = new FabricMetaService(client);

        await ExpectInvalidDataAsync(
            () => service.GetLoaderVersionsAsync("1.21.4"),
            "Declared oversized Fabric loader list");
        Assert(!oversized.ReadAttempted,
            "Declared oversized Fabric loader list must be rejected before body reads.");
    }

    private static async Task TestDeclaredProfileLimitAsync()
    {
        var oversized = new DeclaredLengthContent(MetadataLimit + 1L);
        using var client = new HttpClient(new FixedContentHandler(oversized));
        var service = new FabricMetaService(client);

        await ExpectInvalidDataAsync(
            async () =>
            {
                using var _ = await service.GetProfileAsync("1.21.4", "0.16.9");
            },
            "Declared oversized Fabric profile");
        Assert(!oversized.ReadAttempted,
            "Declared oversized Fabric profile must be rejected before body reads.");
    }

    private static async Task TestUnknownLengthLimitAsync()
    {
        var stream = new GeneratingStream(MetadataLimit + 1L);
        using var client = new HttpClient(new FixedContentHandler(new StreamContent(stream)));
        var service = new FabricMetaService(client);

        await ExpectInvalidDataAsync(
            () => service.GetLoaderVersionsAsync("1.21.4"),
            "Unknown-length oversized Fabric loader list");
        Assert(stream.BytesRead > MetadataLimit,
            "Unknown-length Fabric Meta response must cross the configured limit.");
    }

    private static async Task TestJustUnderLimitAsync()
    {
        const string core =
            "[{\"loader\":{\"version\":\"0.16.9\",\"stable\":true}}]";
        var body = core.PadRight(MetadataLimit - 1, ' ');
        using var client = new HttpClient(new FixedContentHandler(
            new StringContent(body, Encoding.UTF8, "application/json")));
        var versions = await new FabricMetaService(client)
            .GetLoaderVersionsAsync("1.21.4");

        Assert(versions.Count == 1 && versions[0].Version == "0.16.9",
            "Just-under-limit Fabric Meta response should remain accepted.");
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

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class FixedContentHandler(HttpContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            });
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
}
