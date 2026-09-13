using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class StabilityRegression
{
    [ModuleInitializer]
    public static void RunBeforeMain() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        await TestVanillaBodyIdleFallbackAsync();
        await TestVanillaUserCancellationDoesNotFallbackAsync();
        await TestDamagedManagedRuntimeIsRejectedAsync();
        await TestManagedJavaBodyIdleTimeoutAsync();
        Console.WriteLine("PASS stability regressions");
    }

    private static async Task TestVanillaBodyIdleFallbackAsync()
    {
        var temp = Path.Combine(Path.GetTempPath(), "nexo-stall-fallback", Guid.NewGuid().ToString("N"));
        try
        {
            var sources = new DownloadSourceService();
            sources.SetSource("bmclapi");
            var handler = new StallThenOfficialHandler();
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            var paths = new NexoPathService(temp);
            var service = new MinecraftVanillaInstallService(client, paths, sources, TimeSpan.FromMilliseconds(50));
            var instance = new GameInstance("stall-test", "Stall fallback", "stall-test", "vanilla", DateTimeOffset.UtcNow);
            var version = new MinecraftVersionInfo("stall-test", "release", "https://piston-meta.mojang.com/v1/packages/test/stall-test.json", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, string.Empty, 0);

            await service.InstallAsync(instance, version);
            Equal(2, handler.Hosts.Count, "stalled mirror must fall back to official source");
            Equal("bmclapi2.bangbang93.com", handler.Hosts[0], "mirror should be attempted first");
            Equal("piston-meta.mojang.com", handler.Hosts[1], "official source should follow idle timeout");
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
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            var service = new MinecraftVanillaInstallService(client, new NexoPathService(temp), sources, TimeSpan.FromSeconds(5));
            var instance = new GameInstance("cancel-test", "Cancel", "cancel-test", "vanilla", DateTimeOffset.UtcNow);
            var version = new MinecraftVersionInfo("cancel-test", "release", "https://piston-meta.mojang.com/v1/packages/test/cancel-test.json", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, string.Empty, 0);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

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
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            var service = new JavaRuntimeProvisionService(client, new NexoPathService(temp), TimeSpan.FromMilliseconds(50), (_, _, _) => Task.FromResult(true));
            try
            {
                await service.EnsureJavaAsync(8);
                throw new InvalidOperationException("Expected stalled Java package body to time out.");
            }
            catch (TimeoutException) { }
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
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri?.Host ?? string.Empty;
            Hosts.Add(host);
            if (host.Equals("bmclapi2.bangbang93.com", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NeverProgressStream()) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"test\",\"type\":\"release\",\"libraries\":[]}", Encoding.UTF8, "application/json") });
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
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
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
