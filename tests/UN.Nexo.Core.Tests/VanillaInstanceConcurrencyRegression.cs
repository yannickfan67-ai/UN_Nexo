using System.Net;
using System.Text;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class VanillaInstanceConcurrencyRegression
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-vanilla-instance-concurrency",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var aGate = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var bGate = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new ConcurrentPrepareHandler(
                aGate.Task,
                bGate.Task);
            using var http = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            var paths = new NexoPathService(root);
            var service = new MinecraftVanillaInstallService(
                http,
                paths,
                new DownloadSourceService(),
                TimeSpan.FromSeconds(3));

            var a = Instance("concurrency-a");
            var b = Instance("concurrency-b");
            Task installA;
            Task installB;
            using (ExecutionContext.SuppressFlow())
            {
                installA = Task.Run(() =>
                    service.InstallAsync(
                        a,
                        Version(a.VersionId)));
                installB = Task.Run(() =>
                    service.InstallAsync(
                        b,
                        Version(b.VersionId)));
            }

            await Task.WhenAll(
                handler.AMetadataRequested.Task,
                handler.BMetadataRequested.Task)
                .WaitAsync(TimeSpan.FromSeconds(2));

            Assert(
                service.IsInstalling,
                "Two active per-instance preparations should report installation activity.");
            Assert(
                !service.CancelCurrentInstall(),
                "Ambiguous parameterless cancellation must not cancel one of multiple active instances.");
            Assert(
                service.CancelInstall(a.Id),
                "Instance-targeted cancellation should find preparation A.");

            bGate.TrySetResult();

            try
            {
                await installA.WaitAsync(
                    TimeSpan.FromSeconds(2));
                throw new Exception(
                    "Cancelled instance A unexpectedly completed.");
            }
            catch (OperationCanceledException)
            {
            }

            await installB.WaitAsync(
                TimeSpan.FromSeconds(2));

            Assert(
                File.Exists(Path.Combine(
                    paths.GetInstanceDirectory(b.Id),
                    "install-state.json")),
                "Cancelling A must not prevent independent instance B from preparing.");
            Assert(
                !File.Exists(Path.Combine(
                    paths.GetInstanceDirectory(a.Id),
                    "install-state.json")),
                "Cancelled instance A must not publish prepared state.");
            Assert(
                !service.IsInstalling,
                "All completed/cancelled preparations should clear activity state.");
        }
        finally
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
    }

    private static GameInstance Instance(
        string versionId)
        => new(
            Guid.NewGuid().ToString("N"),
            versionId,
            versionId,
            "vanilla",
            DateTimeOffset.UtcNow);

    private static MinecraftVersionInfo Version(
        string versionId)
        => new(
            versionId,
            "release",
            $"https://piston-meta.mojang.com/{versionId}.json",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            string.Empty,
            0);

    private static void Assert(
        bool condition,
        string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class ConcurrentPrepareHandler(
        Task aGate,
        Task bGate) : HttpMessageHandler
    {
        public TaskCompletionSource AMetadataRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BMetadataRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri
                ?? throw new InvalidOperationException(
                    "Missing request URI.");

            if (uri.AbsolutePath.EndsWith(
                    "/concurrency-a.json",
                    StringComparison.Ordinal))
            {
                AMetadataRequested.TrySetResult();
                return Task.FromResult(
                    GatedMetadata(
                        "concurrency-a",
                        aGate));
            }

            if (uri.AbsolutePath.EndsWith(
                    "/concurrency-b.json",
                    StringComparison.Ordinal))
            {
                BMetadataRequested.TrySetResult();
                return Task.FromResult(
                    GatedMetadata(
                        "concurrency-b",
                        bGate));
            }

            if (uri.AbsolutePath.EndsWith(
                    "-client.jar",
                    StringComparison.Ordinal))
            {
                return Task.FromResult(
                    new HttpResponseMessage(
                        HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(
                            Encoding.UTF8.GetBytes(
                                "client"))
                    });
            }

            if (uri.AbsolutePath.EndsWith(
                    "-assets.json",
                    StringComparison.Ordinal))
            {
                return Task.FromResult(
                    new HttpResponseMessage(
                        HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            "{\"objects\":{}}",
                            Encoding.UTF8,
                            "application/json")
                    });
            }

            return Task.FromResult(
                new HttpResponseMessage(
                    HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage GatedMetadata(
            string versionId,
            Task gate)
        {
            var metadata =
                "{\"id\":\""
                + versionId
                + "\",\"libraries\":[],"
                + "\"downloads\":{\"client\":{\"url\":\"https://piston-data.mojang.com/"
                + versionId
                + "-client.jar\",\"size\":6}},"
                + "\"assetIndex\":{\"id\":\""
                + versionId
                + "-assets\",\"url\":\"https://launchermeta.mojang.com/"
                + versionId
                + "-assets.json\"}}";
            var bytes = Encoding.UTF8.GetBytes(
                metadata);
            return new HttpResponseMessage(
                HttpStatusCode.OK)
            {
                Content = new StreamContent(
                    new GateStream(
                        bytes,
                        gate))
            };
        }
    }

    private sealed class GateStream(
        byte[] bytes,
        Task gate) : Stream
    {
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length =>
            throw new NotSupportedException();
        public override long Position
        {
            get => _offset;
            set => throw new NotSupportedException();
        }

        public override int Read(
            byte[] buffer,
            int offset,
            int count)
            => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await gate.WaitAsync(cancellationToken);
            if (_offset >= bytes.Length)
                return 0;

            var count = Math.Min(
                buffer.Length,
                bytes.Length - _offset);
            bytes.AsMemory(
                    _offset,
                    count)
                .CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(
            long offset,
            SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(
            byte[] buffer,
            int offset,
            int count)
            => throw new NotSupportedException();
    }
}
