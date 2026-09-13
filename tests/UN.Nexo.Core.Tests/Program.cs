using System.Net;
using System.Net.Http;
using UN.Nexo.Core.Launching;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class Program
{
    private static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("Java major parsing", TestJavaMajorAsync),
            ("Manifest streaming fallback", TestManifestStreamingFallbackAsync),
            ("Modern 1.21.4 launch plan", () => TestLaunchPlanAsync("1.21.4", 21, modern: true)),
            ("Legacy 1.8.9 launch plan", () => TestLaunchPlanAsync("1.8.9", 8, modern: false))
        };

        var failures = 0;
        foreach (var test in tests)
        {
            try
            {
                await test.Run();
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {test.Name}: {ex}");
            }
        }

        Console.WriteLine($"Regression checks: {tests.Length - failures}/{tests.Length} passed");
        return failures == 0 ? 0 : 1;
    }

    private static Task TestJavaMajorAsync()
    {
        Equal(8, MinecraftLaunchPlanBuilder.JavaMajor("1.8.0_442"), "Java 8 parsing");
        Equal(17, MinecraftLaunchPlanBuilder.JavaMajor("17.0.13"), "Java 17 parsing");
        Equal(21, MinecraftLaunchPlanBuilder.JavaMajor("21.0.8+9"), "Java 21 parsing");
        return Task.CompletedTask;
    }

    private static async Task TestManifestStreamingFallbackAsync()
    {
        var sources = new DownloadSourceService();
        sources.SetSource("bmclapi");
        var handler = new ManifestFallbackHandler();
        using var client = new HttpClient(handler);
        var service = new MinecraftVersionManifestService(client, sources);

        var catalog = await service.GetCatalogAsync();
        Equal("1.21.4", catalog.Latest.LatestRelease, "official fallback release");
        Equal(2, handler.Hosts.Count, "both mirror and official should be attempted");
        Equal("bmclapi2.bangbang93.com", handler.Hosts[0], "mirror should be first");
        Equal("launchermeta.mojang.com", handler.Hosts[1], "official should be fallback");
    }

    private static async Task TestLaunchPlanAsync(string version, int expectedJava, bool modern)
    {
        var temp = Path.Combine(Path.GetTempPath(), "UN Nexo regression with spaces", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new NexoPathService(temp);
            var instance = new GameInstance("test-instance", $"Minecraft {version}", version, "vanilla", DateTimeOffset.UtcNow);
            var accountUuid = Guid.NewGuid();
            var account = new LauncherAccount("local-test", "offline", "NexoTester", accountUuid.ToString(), DateTimeOffset.UtcNow);
            var instanceRoot = paths.GetInstanceDirectory(instance.Id);
            var gameRoot = paths.GetInstanceGameDirectory(instance.Id);
            var versionRoot = Path.Combine(gameRoot, "versions", version);
            var assetsRoot = Path.Combine(gameRoot, "assets");
            Directory.CreateDirectory(versionRoot);
            Directory.CreateDirectory(Path.Combine(assetsRoot, "indexes"));
            await File.WriteAllTextAsync(Path.Combine(instanceRoot, "install-state.json"), "{}");
            await File.WriteAllBytesAsync(Path.Combine(versionRoot, version + ".jar"), [1]);

            var assetId = modern ? "17" : "legacy";
            await File.WriteAllTextAsync(Path.Combine(assetsRoot, "indexes", assetId + ".json"),
                modern ? "{\"objects\":{}}" : "{\"virtual\":true,\"objects\":{}}");

            var metadata = modern
                ? $$"""
                {
                  "id":"{{version}}",
                  "type":"release",
                  "javaVersion":{"majorVersion":21},
                  "downloads":{"client":{}},
                  "assetIndex":{"id":"{{assetId}}"},
                  "libraries":[],
                  "mainClass":"net.minecraft.client.main.Main",
                  "arguments":{
                    "jvm":["-Djava.library.path=${natives_directory}","-cp","${classpath}","-Dnexo.launcher=${launcher_version}"],
                    "game":["--username","${auth_player_name}","--version","${version_name}","--gameDir","${game_directory}","--assetsDir","${assets_root}","--assetIndex","${assets_index_name}","--uuid","${auth_uuid}","--accessToken","${auth_access_token}","--userType","${user_type}","--versionType","${version_type}"]
                  }
                }
                """
                : $$"""
                {
                  "id":"{{version}}",
                  "type":"release",
                  "downloads":{"client":{}},
                  "assetIndex":{"id":"{{assetId}}"},
                  "libraries":[],
                  "mainClass":"net.minecraft.client.main.Main",
                  "minecraftArguments":"--username ${auth_player_name} --version ${version_name} --gameDir \"${game_directory}\" --assetsDir ${game_assets} --assetIndex ${assets_index_name} --uuid ${auth_uuid} --accessToken ${auth_access_token} --userProperties ${user_properties} --userType ${user_type}"
                }
                """;
            await File.WriteAllTextAsync(Path.Combine(versionRoot, version + ".json"), metadata);

            var java8 = Path.Combine(temp, "java8");
            var java21 = Path.Combine(temp, "java21");
            await File.WriteAllTextAsync(java8, "test");
            await File.WriteAllTextAsync(java21, "test");
            var installations = new[]
            {
                new JavaInstallation(java21, temp, "21.0.8", true, "test"),
                new JavaInstallation(java8, temp, "1.8.0_442", true, "test")
            };

            var plan = await new MinecraftLaunchPlanBuilder(paths).BuildAsync(instance, account, installations);
            Equal(expectedJava == 21 ? java21 : java8, plan.JavaPath, "required Java selection");
            Equal(gameRoot, plan.WorkingDirectory, "instance working directory");
            Contains(plan.Arguments, "NexoTester", "username substitution");
            Contains(plan.Arguments, accountUuid.ToString("N"), "UUID substitution");
            Contains(plan.Arguments, gameRoot, "game directory should remain one argument even with spaces");
            Contains(plan.Arguments, "net.minecraft.client.main.Main", "main class");
            if (modern)
                Contains(plan.Arguments, "-Dnexo.launcher=0.4.0-dev", "launcher version substitution");
            else
                Contains(plan.Arguments, "-Djava.library.path=" + Path.Combine(gameRoot, "natives", version), "legacy native path");

            var startInfo = plan.CreateStartInfo();
            Contains(startInfo.ArgumentList, gameRoot, "ProcessStartInfo.ArgumentList must preserve spaced path");
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

    private static void Contains(IEnumerable<string> values, string expected, string message)
    {
        if (!values.Contains(expected, StringComparer.Ordinal))
            throw new InvalidOperationException($"{message}: missing '{expected}'.");
    }

    private sealed class ManifestFallbackHandler : HttpMessageHandler
    {
        public List<string> Hosts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri?.Host ?? string.Empty;
            Hosts.Add(host);
            if (host.Equals("bmclapi2.bangbang93.com", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new InterruptedResponseStream())
                });
            }

            const string manifest = "{\"latest\":{\"release\":\"1.21.4\",\"snapshot\":\"25w01a\"},\"versions\":[]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(manifest)
            });
        }
    }

    private sealed class InterruptedResponseStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
            => throw new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely."));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
