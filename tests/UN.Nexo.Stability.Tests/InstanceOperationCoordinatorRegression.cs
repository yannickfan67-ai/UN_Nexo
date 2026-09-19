using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Stability.Tests;

internal static class InstanceOperationCoordinatorRegression
{
    internal static async Task RunAsync()
    {
        await TestDifferentInstancesPrepareConcurrentlyAsync();
        await TestSameInstanceServicesSerializeAndReuseAsync();
        await TestCancellationIsInstanceLocalAsync();
        await TestLifecycleWaitsForSharedLeaseAsync();
    }

    private static async Task TestDifferentInstancesPrepareConcurrentlyAsync()
    {
        var root = NewRoot("different");
        try
        {
            var handler = new BarrierHandler(
                expectedMetadataRequests: 2);
            using var client = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            var paths = new NexoPathService(root);
            var service = new MinecraftVanillaInstallService(
                client,
                paths,
                new DownloadSourceService(),
                TimeSpan.FromSeconds(2));

            var a = Instance("parallel-a");
            var b = Instance("parallel-b");
            using var timeout =
                new CancellationTokenSource(TimeSpan.FromSeconds(5));

            await Task.WhenAll(
                service.InstallAsync(
                    a,
                    Version(a.VersionId),
                    cancellationToken: timeout.Token),
                service.InstallAsync(
                    b,
                    Version(b.VersionId),
                    cancellationToken: timeout.Token));

            Assert(
                handler.MaxConcurrentMetadataRequests >= 2,
                "Different instances should reach metadata download concurrently through one installer service.");
            Assert(
                File.Exists(Path.Combine(
                    paths.GetInstanceDirectory(a.Id),
                    "install-state.json"))
                && File.Exists(Path.Combine(
                    paths.GetInstanceDirectory(b.Id),
                    "install-state.json")),
                "Both independently coordinated instances should prepare successfully.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task TestSameInstanceServicesSerializeAndReuseAsync()
    {
        var root = NewRoot("same");
        try
        {
            var firstMetadataEntered =
                new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstMetadata =
                new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new SerializingHandler(
                firstMetadataEntered,
                releaseFirstMetadata);
            using var client = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            var paths = new NexoPathService(root);
            var sources = new DownloadSourceService();
            var firstService =
                new MinecraftVanillaInstallService(
                    client,
                    paths,
                    sources,
                    TimeSpan.FromSeconds(2));
            var secondService =
                new MinecraftVanillaInstallService(
                    client,
                    paths,
                    sources,
                    TimeSpan.FromSeconds(2));
            var instance = Instance("serialized");
            var version = Version(instance.VersionId);
            using var timeout =
                new CancellationTokenSource(TimeSpan.FromSeconds(5));

            var first = firstService.InstallAsync(
                instance,
                version,
                cancellationToken: timeout.Token);
            await firstMetadataEntered.Task.WaitAsync(
                timeout.Token);

            var second = secondService.InstallAsync(
                instance,
                version,
                cancellationToken: timeout.Token);
            await Task.Delay(
                150,
                timeout.Token);

            Assert(
                handler.MetadataRequests == 1,
                "A second service targeting the same instance must wait before making its first HTTP request.");

            releaseFirstMetadata.TrySetResult(true);
            await Task.WhenAll(first, second);

            Assert(
                handler.MetadataRequests == 2,
                "The queued same-instance retry should run after the first preparation releases the lease.");
            Assert(
                handler.ClientRequests == 1,
                "The queued retry should re-check and reuse the SHA-1 verified client produced by the first preparation.");

            var gameRoot =
                paths.GetInstanceGameDirectory(instance.Id);
            Assert(
                !Directory.EnumerateFiles(
                        gameRoot,
                        "*.part",
                        SearchOption.AllDirectories)
                    .Any(),
                "Completed same-instance preparations must not leave shared fixed .part staging files.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task TestCancellationIsInstanceLocalAsync()
    {
        var root = NewRoot("cancel");
        try
        {
            var handler = new CancellationHandler();
            using var client = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            var paths = new NexoPathService(root);
            var service = new MinecraftVanillaInstallService(
                client,
                paths,
                new DownloadSourceService(),
                TimeSpan.FromSeconds(5));
            var a = Instance("cancel-a");
            var b = Instance("cancel-b");
            using var aCancellation =
                new CancellationTokenSource();
            using var timeout =
                new CancellationTokenSource(TimeSpan.FromSeconds(5));

            var aInstall = service.InstallAsync(
                a,
                Version(a.VersionId),
                cancellationToken: aCancellation.Token);
            await handler.CancelMetadataEntered.Task.WaitAsync(
                timeout.Token);

            var bInstall = service.InstallAsync(
                b,
                Version(b.VersionId),
                cancellationToken: timeout.Token);

            aCancellation.Cancel();
            try
            {
                await aInstall;
                throw new Exception(
                    "Cancelled instance A unexpectedly completed.");
            }
            catch (OperationCanceledException) when (
                aCancellation.IsCancellationRequested)
            {
            }

            await bInstall;
            Assert(
                File.Exists(Path.Combine(
                    paths.GetInstanceDirectory(b.Id),
                    "install-state.json")),
                "Cancelling instance A must not cancel unrelated instance B.");
            Assert(
                !File.Exists(Path.Combine(
                    paths.GetInstanceDirectory(a.Id),
                    "install-state.json")),
                "Cancelled instance A must not publish prepared state.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task TestLifecycleWaitsForSharedLeaseAsync()
    {
        var root = NewRoot("lifecycle");
        try
        {
            var paths = new NexoPathService(root);
            paths.EnsureDirectories();
            var instance = Instance("lifecycle-lock");
            var saves = Path.Combine(
                paths.GetInstanceGameDirectory(instance.Id),
                "saves",
                "World");
            Directory.CreateDirectory(saves);
            await File.WriteAllTextAsync(
                Path.Combine(saves, "level.dat"),
                "world-state");

            var coordinator =
                new InstanceOperationCoordinator(paths);
            var lifecycle =
                new InstanceLifecycleService(paths);
            var lease = await coordinator.AcquireAsync(
                instance.Id,
                "test-holder");

            Task<WorldBackupInfo>? backup = null;
            try
            {
                backup = Task.Run(
                    () => lifecycle.CreateWorldBackupAsync(
                        instance));
                await Task.Delay(150);
                Assert(
                    !backup.IsCompleted,
                    "Lifecycle backup should wait while another operation holds the same instance lease.");
            }
            finally
            {
                await lease.DisposeAsync();
            }

            var completed = await backup.WaitAsync(
                TimeSpan.FromSeconds(5));
            Assert(
                File.Exists(completed.FilePath),
                "Backup should continue successfully after the shared lease is released.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static GameInstance Instance(string versionId)
        => new(
            Guid.NewGuid().ToString("N"),
            versionId,
            versionId,
            "vanilla",
            DateTimeOffset.UtcNow);

    private static MinecraftVersionInfo Version(string id)
        => new(
            id,
            "release",
            $"https://piston-meta.mojang.com/{id}.json",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            string.Empty,
            0);

    private static string Metadata(
        string id,
        byte[] client,
        byte[] index)
        => JsonSerializer.Serialize(new
        {
            id,
            type = "release",
            mainClass =
                "net.minecraft.client.main.Main",
            javaVersion = new
            {
                majorVersion = 21
            },
            downloads = new
            {
                client = new
                {
                    url =
                        $"https://piston-data.mojang.com/{id}-client.jar",
                    size = client.LongLength,
                    sha1 = Sha1(client)
                }
            },
            assetIndex = new
            {
                id = id + "-assets",
                url =
                    $"https://launchermeta.mojang.com/{id}-assets.json",
                size = index.LongLength,
                sha1 = Sha1(index)
            },
            libraries = Array.Empty<object>(),
            arguments = new
            {
                jvm = Array.Empty<string>(),
                game = Array.Empty<string>()
            }
        });

    private static string Sha1(byte[] value)
        => Convert.ToHexString(
                SHA1.HashData(value))
            .ToLowerInvariant();

    private static HttpResponseMessage Bytes(
        byte[] value,
        string mediaType = "application/octet-stream")
    {
        var content = new ByteArrayContent(value);
        content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue(
                mediaType);
        return new HttpResponseMessage(
            HttpStatusCode.OK)
        {
            Content = content
        };
    }

    private static HttpResponseMessage Json(string value)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                value,
                Encoding.UTF8,
                "application/json")
        };

    private static string IdFromPath(
        string absolutePath,
        string suffix)
    {
        var name = absolutePath.TrimStart('/');
        if (!name.EndsWith(
                suffix,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Unexpected fixture path: " + absolutePath);
        return name[..^suffix.Length];
    }

    private static string NewRoot(string suffix)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-instance-operation-tests",
            suffix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(
                    root,
                    recursive: true);
        }
        catch
        {
        }
    }

    private static void Assert(
        bool condition,
        string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class BarrierHandler(
        int expectedMetadataRequests)
        : HttpMessageHandler
    {
        private readonly TaskCompletionSource<bool> _barrier =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _metadataActive;
        private int _metadataRequests;
        private int _maxConcurrentMetadataRequests;

        public int MaxConcurrentMetadataRequests =>
            Volatile.Read(
                ref _maxConcurrentMetadataRequests);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri
                ?? throw new InvalidOperationException(
                    "Missing request URI.");
            if (uri.Host.Equals(
                    "piston-meta.mojang.com",
                    StringComparison.OrdinalIgnoreCase))
            {
                var id = IdFromPath(
                    uri.AbsolutePath,
                    ".json");
                var client = Encoding.UTF8.GetBytes(
                    id + "-client");
                var index = Encoding.UTF8.GetBytes(
                    "{\"objects\":{}}");

                var active = Interlocked.Increment(
                    ref _metadataActive);
                UpdateMax(
                    ref _maxConcurrentMetadataRequests,
                    active);
                var count = Interlocked.Increment(
                    ref _metadataRequests);
                if (count >= expectedMetadataRequests)
                    _barrier.TrySetResult(true);

                try
                {
                    await _barrier.Task.WaitAsync(
                        cancellationToken);
                }
                finally
                {
                    Interlocked.Decrement(
                        ref _metadataActive);
                }

                return Json(
                    Metadata(id, client, index));
            }

            if (uri.Host.Equals(
                    "piston-data.mojang.com",
                    StringComparison.OrdinalIgnoreCase))
            {
                var id = IdFromPath(
                    uri.AbsolutePath,
                    "-client.jar");
                return Bytes(
                    Encoding.UTF8.GetBytes(
                        id + "-client"));
            }

            if (uri.Host.Equals(
                    "launchermeta.mojang.com",
                    StringComparison.OrdinalIgnoreCase))
                return Bytes(
                    Encoding.UTF8.GetBytes(
                        "{\"objects\":{}}"),
                    "application/json");

            return new HttpResponseMessage(
                HttpStatusCode.NotFound);
        }

        private static void UpdateMax(
            ref int target,
            int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref target);
                if (value <= current)
                    return;
                if (Interlocked.CompareExchange(
                        ref target,
                        value,
                        current) == current)
                    return;
            }
        }
    }

    private sealed class SerializingHandler(
        TaskCompletionSource<bool> firstEntered,
        TaskCompletionSource<bool> releaseFirst)
        : HttpMessageHandler
    {
        private int _metadataRequests;
        private int _clientRequests;

        public int MetadataRequests =>
            Volatile.Read(ref _metadataRequests);
        public int ClientRequests =>
            Volatile.Read(ref _clientRequests);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri
                ?? throw new InvalidOperationException(
                    "Missing request URI.");
            const string id = "serialized";
            var client = Encoding.UTF8.GetBytes(
                id + "-client");
            var index = Encoding.UTF8.GetBytes(
                "{\"objects\":{}}");

            if (uri.Host.Equals(
                    "piston-meta.mojang.com",
                    StringComparison.OrdinalIgnoreCase))
            {
                var count = Interlocked.Increment(
                    ref _metadataRequests);
                if (count == 1)
                {
                    firstEntered.TrySetResult(true);
                    await releaseFirst.Task.WaitAsync(
                        cancellationToken);
                }

                return Json(
                    Metadata(id, client, index));
            }

            if (uri.Host.Equals(
                    "piston-data.mojang.com",
                    StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(
                    ref _clientRequests);
                return Bytes(client);
            }

            if (uri.Host.Equals(
                    "launchermeta.mojang.com",
                    StringComparison.OrdinalIgnoreCase))
                return Bytes(
                    index,
                    "application/json");

            return new HttpResponseMessage(
                HttpStatusCode.NotFound);
        }
    }

    private sealed class CancellationHandler
        : HttpMessageHandler
    {
        public TaskCompletionSource<bool> CancelMetadataEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri
                ?? throw new InvalidOperationException(
                    "Missing request URI.");

            if (uri.Host.Equals(
                    "piston-meta.mojang.com",
                    StringComparison.OrdinalIgnoreCase))
            {
                var id = IdFromPath(
                    uri.AbsolutePath,
                    ".json");
                if (id == "cancel-a")
                {
                    CancelMetadataEntered.TrySetResult(true);
                    await Task.Delay(
                        Timeout.InfiniteTimeSpan,
                        cancellationToken);
                }

                var client = Encoding.UTF8.GetBytes(
                    id + "-client");
                var index = Encoding.UTF8.GetBytes(
                    "{\"objects\":{}}");
                return Json(
                    Metadata(id, client, index));
            }

            if (uri.Host.Equals(
                    "piston-data.mojang.com",
                    StringComparison.OrdinalIgnoreCase))
            {
                var id = IdFromPath(
                    uri.AbsolutePath,
                    "-client.jar");
                return Bytes(
                    Encoding.UTF8.GetBytes(
                        id + "-client"));
            }

            if (uri.Host.Equals(
                    "launchermeta.mojang.com",
                    StringComparison.OrdinalIgnoreCase))
                return Bytes(
                    Encoding.UTF8.GetBytes(
                        "{\"objects\":{}}"),
                    "application/json");

            return new HttpResponseMessage(
                HttpStatusCode.NotFound);
        }
    }
}
