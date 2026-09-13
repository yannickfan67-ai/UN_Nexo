using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using UN.Nexo.Core.Launching;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class CurrentCompatibilityChecks
{
    [ModuleInitializer]
    internal static void RunBeforeMain()
        => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        await TestCurrentAdoptiumSchemaAsync();
        Console.WriteLine("PASS Current Adoptium binaries schema");

        await TestMinecraft152Pre16ResourcesAsync();
        Console.WriteLine("PASS Minecraft 1.5.2 pre-1.6 resources");
    }

    private static async Task TestCurrentAdoptiumSchemaAsync()
    {
        var temp = Path.Combine(Path.GetTempPath(), "UN Nexo Adoptium current schema", Guid.NewGuid().ToString("N"));
        try
        {
            var archive = CreateFakeJavaArchive();
            var checksum = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
            using var client = new HttpClient(new CurrentAdoptiumHandler(archive, checksum));
            var runtime = await new JavaRuntimeProvisionService(client, new NexoPathService(temp)).EnsureJavaAsync(8);

            if (MinecraftLaunchPlanBuilder.JavaMajor(runtime.Version) != 8)
                throw new InvalidOperationException($"Expected managed Java 8, got {runtime.Version}.");
            if (!File.Exists(runtime.JavaPath))
                throw new InvalidOperationException("Managed Java executable was not installed.");
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private static async Task TestMinecraft152Pre16ResourcesAsync()
    {
        const string version = "1.5.2";
        var temp = Path.Combine(Path.GetTempPath(), "UN Nexo 1.5.2 pre-1.6", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new NexoPathService(temp);
            var instance = new GameInstance("mc-152-current", "Minecraft 1.5.2", version, "vanilla", DateTimeOffset.UtcNow);
            var account = new LauncherAccount("local-152-current", "offline", "LegacyTester", Guid.NewGuid().ToString(), DateTimeOffset.UtcNow);
            var instanceRoot = paths.GetInstanceDirectory(instance.Id);
            var gameRoot = paths.GetInstanceGameDirectory(instance.Id);
            var versionRoot = Path.Combine(gameRoot, "versions", version);
            var assetsRoot = Path.Combine(gameRoot, "assets");
            var librariesRoot = Path.Combine(gameRoot, "libraries");
            Directory.CreateDirectory(versionRoot);
            Directory.CreateDirectory(Path.Combine(assetsRoot, "indexes"));
            Directory.CreateDirectory(Path.Combine(assetsRoot, "objects", "aa"));
            await File.WriteAllTextAsync(Path.Combine(instanceRoot, "install-state.json"), "{}");
            await File.WriteAllBytesAsync(Path.Combine(versionRoot, version + ".jar"), [1]);

            const string launchWrapperRelative = "net/minecraft/launchwrapper/1.5/launchwrapper-1.5.jar";
            var launchWrapperPath = Path.Combine(librariesRoot, launchWrapperRelative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(launchWrapperPath)!);
            await File.WriteAllBytesAsync(launchWrapperPath, [2]);

            const string assetHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            var objectPath = Path.Combine(assetsRoot, "objects", "aa", assetHash);
            await File.WriteAllBytesAsync(objectPath, [3]);
            await File.WriteAllTextAsync(
                Path.Combine(assetsRoot, "indexes", "pre-1.6.json"),
                "{\"map_to_resources\":true,\"objects\":{\"icons/icon_16x16.png\":{\"hash\":\"" + assetHash + "\"}}}");

            var metadata = """
                {
                  "id":"1.5.2",
                  "type":"release",
                  "javaVersion":{"component":"jre-legacy","majorVersion":8},
                  "downloads":{"client":{}},
                  "assets":"pre-1.6",
                  "assetIndex":{"id":"pre-1.6"},
                  "libraries":[
                    {
                      "name":"net.minecraft:launchwrapper:1.5",
                      "downloads":{"artifact":{"path":"net/minecraft/launchwrapper/1.5/launchwrapper-1.5.jar","size":1}}
                    }
                  ],
                  "mainClass":"net.minecraft.launchwrapper.Launch",
                  "minecraftArguments":"${auth_player_name} ${auth_session} --gameDir ${game_directory} --assetsDir ${game_assets}"
                }
                """;
            await File.WriteAllTextAsync(Path.Combine(versionRoot, version + ".json"), metadata);

            var java8 = Path.Combine(temp, OperatingSystem.IsWindows() ? "java8.exe" : "java8");
            await File.WriteAllTextAsync(java8, "test");
            var plan = await new MinecraftLaunchPlanBuilder(paths).BuildAsync(
                instance,
                account,
                [new JavaInstallation(java8, temp, "1.8.0_442", true, "test")]);

            var resourcesRoot = Path.Combine(gameRoot, "resources");
            var mappedAsset = Path.Combine(resourcesRoot, "icons", "icon_16x16.png");
            if (!File.Exists(mappedAsset))
                throw new InvalidOperationException("Minecraft 1.5.2 pre-1.6 asset was not mapped into resources/.");
            if (!plan.Arguments.Contains(resourcesRoot, StringComparer.Ordinal))
                throw new InvalidOperationException("Minecraft 1.5.2 ${game_assets} does not point at resources/.");
            if (!plan.Arguments.Contains("net.minecraft.launchwrapper.Launch", StringComparer.Ordinal))
                throw new InvalidOperationException("Minecraft 1.5.2 LaunchWrapper main class is missing.");
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private static byte[] CreateFakeJavaArchive()
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var executableName = OperatingSystem.IsWindows() ? "java.exe" : "java";
            var entry = zip.CreateEntry($"jdk8/bin/{executableName}");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write("fake managed java");
        }
        return memory.ToArray();
    }

    private sealed class CurrentAdoptiumHandler(byte[] archive, string checksum) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri?.Host ?? string.Empty;
            if (host.Equals("api.adoptium.net", StringComparison.OrdinalIgnoreCase))
            {
                var json = "[{\"binaries\":[{\"package\":{\"link\":\"https://runtime.example.test/temurin8.zip\",\"checksum\":\""
                           + checksum
                           + "\"}}],\"version_data\":{\"semver\":\"8.0.504+1\"}}]";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }

            if (host.Equals("runtime.example.test", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(archive)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
