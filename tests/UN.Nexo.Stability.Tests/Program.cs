using System.Net;
using System.Text;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Stability.Tests;

internal static class Program
{
    private static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("Vanilla body idle fallback", TestVanillaBodyIdleFallbackAsync),
            ("Vanilla user cancellation", TestVanillaUserCancellationDoesNotFallbackAsync),
            ("Vanilla download telemetry", TestVanillaDownloadTelemetryAsync),
            ("Installer-owned cancellation", TestInstallerOwnedCancellationAsync),
            ("Damaged managed Java cache", TestDamagedManagedRuntimeIsRejectedAsync),
            ("Managed Java body idle timeout", TestManagedJavaBodyIdleTimeoutAsync)
        };

        var failures = 0;
        foreach (var test in tests)
        {
            Console.WriteLine($"RUN stability: {test.Name}");
            try
            {
                await test.Run();
                Console.WriteLine($"PASS stability: {test.Name}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"FAIL stability: {test.Name}: {ex}");
            }
        }

        Console.WriteLine($"Stability regressions: {tests.Length - failures}/{tests.Length} passed");
        return failures == 0 ? 0 : 1;
    }

    private static async Task TestVanillaBodyIdleFallbackAsync()
    {
        var temp = Path.Combine(Path.GetTempPath(), "nexo-stall-fallback", Guid.NewGuid().ToString("N"));
        try
        {
            var sources = new DownloadSourceService();
            sources.SetSource("bmclapi");
            var handler = new StallThenOfficialHandler();
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            var paths = new NexoPathService(temp);
            var service = new MinecraftVanillaInstallService(client, paths, sources, TimeSpan.FromMilliseconds(100));
            var observed = new List<InstallProgress>();
            service.ProgressChanged += value => observed.Add(value);
            var instance = new GameInstance(Guid.NewGuid().ToString("N"), "Stall fallback", "stall-test", "vanilla", DateTimeOffset.UtcNow);
            var version = new MinecraftVersionInfo("stall-test", "release", "https://piston-meta.mojang.com/v1/packages/test/stall-test.json", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, string.Empty, 0);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));

            await service.InstallAsync(instance, version, cancellationToken: timeout.Token);
            Equal(false, timeout.IsCancellationRequested, "idle timeout must beat the fixture deadline");
            Equal(2, handler.Hosts.Count, "stalled mirror must fall back to official source");
            Equal("bmclapi2.bangbang93.com", handler.Hosts[0], "mirror should be attempted first");
            Equal("piston-meta.mojang.com", handler.Hosts[1], "official source should follow idle timeout");
            Equal(true, observed.Any(value => value.IsFallback && value.Source == "Official"), "fallback transition should be visible to download telemetry");
            Equal(true, File.Exists(Path.Combine(paths.GetInstanceDirectory(instance.Id), "install-state.json")), "fallback install should complete");
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private static async Task TestVanillaUserCancellationDoesNotFallbackAsync()
    {
        var temp = Path.Combine(Path.GetTempPath(), "nexo-cancel-no-fallback", Guid.NewGuid().ToString("N"));
        try
        {
            var sources = new DownloadSourceService();
            sources.SetSource("bmclapi");
            var handler = new StallThenOfficialHandler();
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            var service = new MinecraftVanillaInstallService(client, new NexoPathService(temp), sources, TimeSpan.FromSeconds(5));
            var instance = new GameInstance(Guid.NewGuid().ToString("N"), "Cancel", "cancel-test", "vanilla", DateTimeOffset.UtcNow);
            var version = new MinecraftVersionInfo("cancel-test", "release", "https://piston-meta.mojang.com/v1/packages/test/cancel-test.json", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, string.Empty, 0);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

            try
            {
                await service.InstallAsync(instance, version, cancellationToken: cancellation.Token);
                throw new InvalidOperationException("Expected user cancellation to propagate.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }

            Equal(1, handler.Hosts.Count, "explicit user cancellation must not try fallback");
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private static async Task TestVanillaDownloadTelemetryAsync()
    {
        var temp = Path.Combine(Path.GetTempPath(), "nexo-download-telemetry", Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new OfficialMetadataHandler();
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            var sources = new DownloadSourceService();
            var service = new MinecraftVanillaInstallService(client, new NexoPathService(temp), sources);
            var observed = new List<InstallProgress>();
            var activity = new List<bool>();
            service.ProgressChanged += value => observed.Add(value);
            service.InstallActivityChanged += value => activity.Add(value);
            var instance = new GameInstance(Guid.NewGuid().ToString("N"), "Telemetry", "telemetry-test", "vanilla", DateTimeOffset.UtcNow);
            var version = new MinecraftVersionInfo("telemetry-test", "release", "https://piston-meta.mojang.com/v1/packages/test/telemetry-test.json", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, string.Empty, 0);

            await service.InstallAsync(instance, version);

            Equal(true, observed.Any(value => value.Source == "Official" && value.BytesDownloaded > 0), "telemetry should expose downloaded bytes and source");
            Equal(true, observed.Any(value => value.TotalBytes is > 0), "telemetry should expose response content length when known");
            Equal(true, observed.Any(value => value.BytesPerSecond > 0), "telemetry should expose transfer speed");
            Equal(true, activity.SequenceEqual([true, false]), "install activity should publish start and stop exactly once");
            Equal(false, service.IsInstalling, "completed install should not remain active");
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private static async Task TestInstallerOwnedCancellationAsync()
    {
        var temp = Path.Combine(Path.GetTempPath(), "nexo-installer-cancel", Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new AlwaysStalledHandler();
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            var sources = new DownloadSourceService();
            var service = new MinecraftVanillaInstallService(client, new NexoPathService(temp), sources, TimeSpan.FromSeconds(10));
            var instance = new GameInstance(Guid.NewGuid().ToString("N"), "Owned cancel", "owned-cancel", "vanilla", DateTimeOffset.UtcNow);
            var version = new MinecraftVersionInfo("owned-cancel", "release", "https://piston-meta.mojang.com/v1/packages/test/owned-cancel.json", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, string.Empty, 0);

            var install = service.InstallAsync(instance, version);
            using var fixtureDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (handler.RequestCount == 0 && !fixtureDeadline.IsCancellationRequested)
                await Task.Delay(10, fixtureDeadline.Token);

            Equal(true, service.IsInstalling, "installer should report active while response body is stalled");
            Equal(true, service.CancelCurrentInstall(), "download manager cancellation should cancel the active install");
            try
            {
                await install;
                throw new InvalidOperationException("Expected installer-owned cancellation to propagate.");
            }
            catch (OperationCanceledException) { }

            Equal(false, service.IsInstalling, "cancelled install should clear active state");
            Equal(false, service.CancelCurrentInstall(), "cancelling again after completion should be a no-op");
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private static async Task TestDamagedManagedRuntimeIsRejectedAsync()
    {
        var temp = Path.Combine(Path.GetTempPath(), "nexo-invalid-managed-java", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new NexoPathService(temp);
            paths.EnsureDirectories();
            var os = OperatingSystem.IsWindows() ? "windows" : "linux";
            var targetRoot = Path.Combine(paths.GetRuntimesRoot(), $"temurin-8-{os}-x64");
            var binRoot = Path.Combine(targetRoot, "bin");
            Directory.CreateDirectory(binRoot);
            var javaPath = Path.Combine(binRoot, OperatingSystem.IsWindows() ? "java.exe" : "java");
            await File.WriteAllBytesAsync(javaPath, []);
            await File.WriteAllTextAsync(Path.Combine(targetRoot, "nexo-runtime.json"),
                "{\"major\":8,\"version\":\"8.0.442+6\",\"vendor\":\"Eclipse Temurin\",\"packageUrl\":\"https://example.invalid/java.zip\",\"sha256\":\"" + new string('a', 64) + "\",\"installedAt\":\"2026-09-13T00:00:00Z\"}");

            var handler = new AlwaysNotFoundHandler();
            using var client = new HttpClient(handler);
            var service = new JavaRuntimeProvisionService(client, paths);
            try
            {
                await service.EnsureJavaAsync(8);
                throw new InvalidOperationException("Damaged cached Java must not be accepted.");
            }
            catch (HttpRequestException) { }
            Equal(1, handler.RequestCount, "invalid cached Java should trigger reacquisition");
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private static async Task TestManagedJavaBodyIdleTimeoutAsync()
    {
        var temp = Path.Combine(Path.GetTempPath(), "nexo-java-stall", Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new StalledJavaHandler();
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            var service = new JavaRuntimeProvisionService(client, new NexoPathService(temp), TimeSpan.FromMilliseconds(100), (_, _, _) => Task.FromResult(true));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await service.EnsureJavaAsync(8, cancellationToken: timeout.Token);
                throw new InvalidOperationException("Expected stalled Java package body to time out.");
            }
            catch (TimeoutException) { }
            Equal(false, timeout.IsCancellationRequested, "Java idle timeout must beat the fixture deadline");
            Equal(2, handler.RequestCount, "Java acquisition should reach package body before idle timeout");
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected '{expected}', got '{actual}'.");
    }

    private sealed class StallThenOfficialHandler : HttpMessageHandler
    {
        public List<string> Hosts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri
                ?? throw new InvalidOperationException("Missing request URI.");
            var host = uri.Host;

            if (uri.AbsolutePath.EndsWith(
                    "/stall-test.json",
                    StringComparison.OrdinalIgnoreCase)
                || uri.AbsolutePath.Contains(
                    "/v1/packages/test/stall-test.json",
                    StringComparison.OrdinalIgnoreCase))
            {
                Hosts.Add(host);
                if (host.Equals(
                        "bmclapi2.bangbang93.com",
                        StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StreamContent(new NeverProgressStream())
                    });

                const string metadata =
                    "{\"id\":\"stall-test\",\"type\":\"release\",\"libraries\":[],"
                    + "\"downloads\":{\"client\":{\"url\":\"https://piston-data.mojang.com/stall-client.jar\"}},"
                    + "\"assetIndex\":{\"id\":\"stall-assets\","
                    + "\"url\":\"https://launchermeta.mojang.com/stall-assets.json\"}}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        metadata,
                        Encoding.UTF8,
                        "application/json")
                });
            }

            if (uri.AbsolutePath.EndsWith(
                    "/stall-client.jar",
                    StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(
                        Encoding.UTF8.GetBytes("client"))
                });

            if (uri.AbsolutePath.EndsWith(
                    "/stall-assets.json",
                    StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"objects\":{}}",
                        Encoding.UTF8,
                        "application/json")
                });

            if (host.Equals(
                    "bmclapi2.bangbang93.com",
                    StringComparison.OrdinalIgnoreCase))
            {
                Hosts.Add(host);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new NeverProgressStream())
                });
            }

            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class OfficialMetadataHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri
                ?? throw new InvalidOperationException("Missing request URI.");

            if (uri.AbsolutePath.EndsWith(
                    "/telemetry-test.json",
                    StringComparison.OrdinalIgnoreCase))
            {
                const string metadata =
                    "{\"id\":\"telemetry-test\",\"type\":\"release\",\"libraries\":[],"
                    + "\"downloads\":{\"client\":{\"url\":\"https://piston-data.mojang.com/telemetry-client.jar\"}},"
                    + "\"assetIndex\":{\"id\":\"telemetry-assets\","
                    + "\"url\":\"https://launchermeta.mojang.com/telemetry-assets.json\"}}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        metadata,
                        Encoding.UTF8,
                        "application/json")
                });
            }

            if (uri.AbsolutePath.EndsWith(
                    "/telemetry-client.jar",
                    StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(
                        Encoding.UTF8.GetBytes(
                            "telemetry-client-payload"))
                });

            if (uri.AbsolutePath.EndsWith(
                    "/telemetry-assets.json",
                    StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"objects\":{}}",
                        Encoding.UTF8,
                        "application/json")
                });

            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class AlwaysStalledHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new NeverProgressStream())
            });
        }
    }

    private sealed class AlwaysNotFoundHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class StalledJavaHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var host = request.RequestUri?.Host ?? string.Empty;
            if (host.Equals("api.adoptium.net", StringComparison.OrdinalIgnoreCase))
            {
                var json = "[{\"binary\":{\"package\":{\"link\":\"https://runtime.example.test/temurin8.zip\",\"checksum\":\"" + new string('a', 64) + "\"}},\"version_data\":{\"semver\":\"8.0.442+6\"}}]";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NeverProgressStream()) });
        }
    }

    private sealed class NeverProgressStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}