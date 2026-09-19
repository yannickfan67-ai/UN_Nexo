using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class ManagedJavaConcurrencyRegression
{
    internal static async Task RunAsync()
    {
        await TestSameMajorProvisioningIsSerializedAsync();
        await TestDifferentMajorsProvisionIndependentlyAsync();
    }

    private static async Task TestSameMajorProvisioningIsSerializedAsync()
    {
        var root = NewRoot("same-major");
        try
        {
            var handler = new ConcurrentRuntimeHandler(blockMajor8Metadata: true);
            using var client = new HttpClient(handler);
            var paths = new NexoPathService(root);
            var serviceA = CreateService(client, paths);
            var serviceB = CreateService(client, paths);

            var first = serviceA.EnsureJavaAsync(8);
            await handler.Major8MetadataEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

            var second = serviceB.EnsureJavaAsync(8);
            await Task.Delay(100);
            Assert(handler.MetadataRequests(8) == 1,
                "Second same-major provisioning call must wait before metadata/network work.");

            handler.ReleaseMajor8Metadata.TrySetResult(true);
            var results = await Task.WhenAll(first, second);

            Assert(results[0].JavaPath == results[1].JavaPath,
                "Concurrent same-major callers should receive the same published runtime.");
            Assert(handler.MetadataRequests(8) == 1,
                "Same-major concurrent provisioning should resolve metadata once.");
            Assert(handler.ArchiveRequests(8) == 1,
                "Same-major concurrent provisioning should download the runtime archive once.");
            Assert(File.Exists(results[0].JavaPath),
                "Published managed runtime must remain present after both callers complete.");

            var downloadRoot = Path.Combine(paths.GetRuntimesRoot(), ".downloads");
            if (Directory.Exists(downloadRoot))
            {
                Assert(!Directory.EnumerateFiles(downloadRoot, "*.part", SearchOption.TopDirectoryOnly).Any(),
                    "Concurrent provisioning must not leave shared .part files.");
                Assert(!Directory.EnumerateFiles(downloadRoot, "*.zip", SearchOption.TopDirectoryOnly).Any(),
                    "Concurrent provisioning must not leave shared archive files.");
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task TestDifferentMajorsProvisionIndependentlyAsync()
    {
        var root = NewRoot("different-majors");
        try
        {
            var handler = new ConcurrentRuntimeHandler(waitForBothMajors: true);
            using var client = new HttpClient(handler);
            var paths = new NexoPathService(root);
            var service = CreateService(client, paths);

            var java8 = service.EnsureJavaAsync(8);
            var java17 = service.EnsureJavaAsync(17);

            await Task.WhenAll(
                    handler.Major8MetadataEntered.Task,
                    handler.Major17MetadataEntered.Task)
                .WaitAsync(TimeSpan.FromSeconds(3));

            var results = await Task.WhenAll(java8, java17);
            Assert(results[0].JavaPath != results[1].JavaPath,
                "Different Java majors should publish distinct runtime targets.");
            Assert(handler.MetadataRequests(8) == 1 && handler.MetadataRequests(17) == 1,
                "Different majors should each resolve metadata exactly once.");
            Assert(handler.ArchiveRequests(8) == 1 && handler.ArchiveRequests(17) == 1,
                "Different majors should each download one archive.");
            Assert(File.Exists(results[0].JavaPath) && File.Exists(results[1].JavaPath),
                "Both independently provisioned runtimes must remain published.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static JavaRuntimeProvisionService CreateService(
        HttpClient client,
        NexoPathService paths)
        => new(
            client,
            paths,
            TimeSpan.FromSeconds(2),
            static (_, _, _) => Task.FromResult(true));

    private static string NewRoot(string suffix)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-java-concurrency-tests",
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

    private sealed class ConcurrentRuntimeHandler : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<int, byte[]> _archives = new();
        private readonly ConcurrentDictionary<int, string> _checksums = new();
        private readonly ConcurrentDictionary<int, int> _metadataRequests = new();
        private readonly ConcurrentDictionary<int, int> _archiveRequests = new();
        private readonly bool _blockMajor8Metadata;
        private readonly bool _waitForBothMajors;
        private readonly TaskCompletionSource<bool> _bothMajorsSeen =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentRuntimeHandler(
            bool blockMajor8Metadata = false,
            bool waitForBothMajors = false)
        {
            _blockMajor8Metadata = blockMajor8Metadata;
            _waitForBothMajors = waitForBothMajors;
            foreach (var major in new[] { 8, 17 })
            {
                var archive = CreateArchive(major);
                _archives[major] = archive;
                _checksums[major] = Convert.ToHexString(
                    SHA256.HashData(archive)).ToLowerInvariant();
            }
        }

        public TaskCompletionSource<bool> Major8MetadataEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Major17MetadataEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseMajor8Metadata { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int MetadataRequests(int major)
            => _metadataRequests.TryGetValue(major, out var count) ? count : 0;

        public int ArchiveRequests(int major)
            => _archiveRequests.TryGetValue(major, out var count) ? count : 0;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri
                ?? throw new InvalidOperationException("Missing request URI.");

            if (uri.Host.Equals("api.adoptium.net", StringComparison.OrdinalIgnoreCase))
            {
                var major = ParseMajorFromMetadataPath(uri.AbsolutePath);
                _metadataRequests.AddOrUpdate(major, 1, static (_, count) => count + 1);
                SignalMetadataEntered(major);

                if (_blockMajor8Metadata && major == 8)
                    await ReleaseMajor8Metadata.Task.WaitAsync(cancellationToken);

                if (_waitForBothMajors)
                    await _bothMajorsSeen.Task.WaitAsync(cancellationToken);

                var json =
                    "[{\"binary\":{\"package\":{\"link\":\"https://runtime.example.test/temurin"
                    + major
                    + ".zip\",\"checksum\":\""
                    + _checksums[major]
                    + "\"}},\"version_data\":{\"semver\":\""
                    + major
                    + ".0.1\"}}]";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }

            if (uri.Host.Equals("runtime.example.test", StringComparison.OrdinalIgnoreCase))
            {
                var major = ParseMajorFromArchivePath(uri.AbsolutePath);
                _archiveRequests.AddOrUpdate(major, 1, static (_, count) => count + 1);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(_archives[major])
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private void SignalMetadataEntered(int major)
        {
            if (major == 8)
                Major8MetadataEntered.TrySetResult(true);
            else if (major == 17)
                Major17MetadataEntered.TrySetResult(true);

            if (Major8MetadataEntered.Task.IsCompleted
                && Major17MetadataEntered.Task.IsCompleted)
                _bothMajorsSeen.TrySetResult(true);
        }

        private static int ParseMajorFromMetadataPath(string path)
        {
            foreach (var major in new[] { 8, 17 })
                if (path.Contains($"/feature_releases/{major}/", StringComparison.Ordinal))
                    return major;
            throw new InvalidOperationException("Unexpected Adoptium metadata path: " + path);
        }

        private static int ParseMajorFromArchivePath(string path)
        {
            foreach (var major in new[] { 8, 17 })
                if (path.EndsWith($"temurin{major}.zip", StringComparison.Ordinal))
                    return major;
            throw new InvalidOperationException("Unexpected runtime archive path: " + path);
        }

        private static byte[] CreateArchive(int major)
        {
            using var memory = new MemoryStream();
            using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            {
                var executable = OperatingSystem.IsWindows() ? "java.exe" : "java";
                var entry = archive.CreateEntry($"jdk{major}/bin/{executable}");
                using var writer = new StreamWriter(
                    entry.Open(),
                    new UTF8Encoding(false));
                writer.Write("fake java " + major);
            }
            return memory.ToArray();
        }
    }
}
