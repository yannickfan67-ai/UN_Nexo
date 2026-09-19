using System.Net;
using System.Text;
using System.Text.Json;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class InstallStateAtomicRegression
{
    internal static async Task RunAsync()
    {
        await TestCancelledAtomicWritePreservesDestinationAsync();
        await TestConcurrentAtomicWritesRemainCompleteAsync();
        await TestVanillaPrepareReplacesStateAtomicallyAsync();
    }

    private static async Task TestCancelledAtomicWritePreservesDestinationAsync()
    {
        var root = NewRoot("cancel");
        try
        {
            var path = Path.Combine(root, "install-state.json");
            var original = Encoding.UTF8.GetBytes(
                "{\"id\":\"old\",\"state\":\"prepared\"}");
            await File.WriteAllBytesAsync(path, original);

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try
            {
                await AtomicJsonFile.WriteAsync(
                    path,
                    new { id = "new", state = "prepared" },
                    new JsonSerializerOptions { WriteIndented = true },
                    cancellation.Token);
                throw new Exception("Cancelled atomic JSON write unexpectedly succeeded.");
            }
            catch (OperationCanceledException)
            {
            }

            var afterCancellation = await File.ReadAllBytesAsync(path);
            Assert(
                original.AsSpan().SequenceEqual(afterCancellation),
                "Cancellation before publication must preserve the previous destination bytes.");
            AssertNoTemps(root, path);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task TestConcurrentAtomicWritesRemainCompleteAsync()
    {
        var root = NewRoot("concurrent");
        try
        {
            var path = Path.Combine(root, "install-state.json");
            await AtomicJsonFile.WriteAsync(path, new { generation = -1 });

            using var readerStop = new CancellationTokenSource();
            Exception? readerFailure = null;
            var reader = Task.Run(async () =>
            {
                try
                {
                    while (!readerStop.IsCancellationRequested)
                    {
                        try
                        {
                            var json = await File.ReadAllTextAsync(path);
                            using var parsed = JsonDocument.Parse(json);
                            _ = parsed.RootElement.GetProperty("generation").GetInt32();
                        }
                        catch (Exception ex) when (
                            ex is IOException or UnauthorizedAccessException)
                        {
                            // Windows can briefly deny opening/replacing the path while a
                            // completed file is being atomically published. Retry that access
                            // race; malformed JSON is deliberately not tolerated here.
                        }

                        await Task.Yield();
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    readerFailure = ex;
                }
            });

            var writes = Enumerable.Range(0, 24)
                .Select(generation => AtomicJsonFile.WriteAsync(
                    path,
                    new
                    {
                        generation,
                        payload = new string((char)('a' + generation % 26), 64 * 1024)
                    }))
                .ToArray();
            await Task.WhenAll(writes);
            readerStop.Cancel();
            await reader;

            if (readerFailure is not null)
                throw new Exception(
                    "Concurrent reader observed incomplete atomic JSON.",
                    readerFailure);

            using var final = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var generationValue = final.RootElement.GetProperty("generation").GetInt32();
            Assert(generationValue is >= 0 and < 24,
                "Final atomic JSON must be one complete writer snapshot.");
            AssertNoTemps(root, path);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task TestVanillaPrepareReplacesStateAtomicallyAsync()
    {
        var root = NewRoot("vanilla");
        try
        {
            var paths = new NexoPathService(root);
            var instance = new GameInstance(
                Guid.NewGuid().ToString("N"),
                "Atomic Vanilla",
                "1.21.4",
                "vanilla",
                DateTimeOffset.UtcNow);
            var instanceRoot = paths.GetInstanceDirectory(instance.Id);
            Directory.CreateDirectory(instanceRoot);
            var statePath = Path.Combine(instanceRoot, "install-state.json");
            await File.WriteAllTextAsync(
                statePath,
                "{\"id\":\"old-instance\",\"state\":\"prepared\"}");

            using var client = new HttpClient(new VanillaStateHandler());
            var installer = new MinecraftVanillaInstallService(
                client,
                paths,
                new DownloadSourceService(),
                TimeSpan.FromSeconds(2));
            var version = new MinecraftVersionInfo(
                "1.21.4",
                "release",
                "https://piston-meta.mojang.com/state-test.json",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                string.Empty,
                0);

            await installer.InstallAsync(instance, version);

            using var state = JsonDocument.Parse(await File.ReadAllTextAsync(statePath));
            var rootElement = state.RootElement;
            var storedId = rootElement.TryGetProperty("Id", out var legacyId)
                ? legacyId.GetString()
                : rootElement.GetProperty("id").GetString();
            Assert(
                storedId == instance.Id,
                "Vanilla Prepare should atomically replace install state with the current instance id.");
            Assert(
                rootElement.GetProperty("state").GetString() == "prepared",
                "Vanilla Prepare should publish a complete prepared marker.");
            AssertNoTemps(instanceRoot, statePath);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void AssertNoTemps(string directory, string destination)
    {
        var prefix = Path.GetFileName(destination) + ".";
        Assert(
            !Directory.EnumerateFiles(directory, "*.tmp", SearchOption.TopDirectoryOnly)
                .Any(file => Path.GetFileName(file).StartsWith(prefix, StringComparison.Ordinal)),
            "Atomic JSON publication must clean operation-specific temporary files.");
    }

    private static string NewRoot(string suffix)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-install-state-atomic-tests",
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

    private sealed class VanillaStateHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri
                ?? throw new InvalidOperationException("Missing request URI.");

            if (uri.Host.Equals("piston-meta.mojang.com", StringComparison.OrdinalIgnoreCase))
            {
                const string metadata =
                    "{\"id\":\"1.21.4\",\"downloads\":{\"client\":{"
                    + "\"url\":\"https://piston-data.mojang.com/client.jar\"}},"
                    + "\"assetIndex\":{\"id\":\"state-test-assets\","
                    + "\"url\":\"https://launchermeta.mojang.com/assets.json\"},"
                    + "\"libraries\":[]}";
                return Task.FromResult(Text(metadata, "application/json"));
            }

            if (uri.Host.Equals("piston-data.mojang.com", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(Bytes(Encoding.UTF8.GetBytes("client")));

            if (uri.Host.Equals("launchermeta.mojang.com", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(Text("{\"objects\":{}}", "application/json"));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Text(string value, string mediaType)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(value, Encoding.UTF8, mediaType)
            };

        private static HttpResponseMessage Bytes(byte[] value)
            => new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(value)
            };
    }
}
