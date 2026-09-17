using System.Net;
using System.Text;
using UN.Nexo.Core.Launching;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

var root = Path.Combine(Path.GetTempPath(), "un-nexo-fabric-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    await UN.Nexo.Fabric.Tests.InstanceModServiceRegression.RunAsync();
    await RunInheritedLaunchRegressionAsync(root);
    await RunInheritanceLoopRegressionAsync(root);
    await RunFabricMetaRegressionAsync();
    RunMavenPathRegression();
    Console.WriteLine("Fabric regression checks passed.");
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
}

static async Task RunInheritedLaunchRegressionAsync(string root)
{
    var paths = new NexoPathService(root);
    paths.EnsureDirectories();
    var instance = new GameInstance(
        "fabric-instance",
        "Fabric test",
        "fabric-loader-0.16.9-1.21.4",
        "fabric",
        DateTimeOffset.UtcNow,
        "1.21.4",
        "0.16.9");
    var gameRoot = paths.GetInstanceGameDirectory(instance.Id);
    var baseVersionRoot = Path.Combine(gameRoot, "versions", "1.21.4");
    var childVersionRoot = Path.Combine(gameRoot, "versions", instance.VersionId);
    var librariesRoot = Path.Combine(gameRoot, "libraries");
    var assetsRoot = Path.Combine(gameRoot, "assets");
    Directory.CreateDirectory(baseVersionRoot);
    Directory.CreateDirectory(childVersionRoot);
    Directory.CreateDirectory(librariesRoot);
    Directory.CreateDirectory(Path.Combine(assetsRoot, "indexes"));

    var baseLibraryRelative = Path.Combine("com", "example", "base", "1.0", "base-1.0.jar");
    var baseLibrary = Path.Combine(librariesRoot, baseLibraryRelative);
    Directory.CreateDirectory(Path.GetDirectoryName(baseLibrary)!);
    await File.WriteAllBytesAsync(baseLibrary, [1]);

    var fabricLibraryRelative = MavenArtifactPath.FromCoordinate("net.fabricmc:fabric-loader:0.16.9");
    var fabricLibrary = Path.Combine(librariesRoot, fabricLibraryRelative);
    Directory.CreateDirectory(Path.GetDirectoryName(fabricLibrary)!);
    await File.WriteAllBytesAsync(fabricLibrary, [2]);

    await File.WriteAllBytesAsync(Path.Combine(baseVersionRoot, "1.21.4.jar"), [3]);
    await File.WriteAllTextAsync(
        Path.Combine(assetsRoot, "indexes", "test-assets.json"),
        "{\"objects\":{}}");

    await File.WriteAllTextAsync(
        Path.Combine(baseVersionRoot, "1.21.4.json"),
        """
        {
          "id": "1.21.4",
          "type": "release",
          "mainClass": "net.minecraft.client.main.Main",
          "javaVersion": { "majorVersion": 17 },
          "downloads": { "client": { "size": 1 } },
          "assetIndex": { "id": "test-assets" },
          "libraries": [
            {
              "name": "com.example:base:1.0",
              "downloads": {
                "artifact": {
                  "path": "com/example/base/1.0/base-1.0.jar",
                  "size": 1
                }
              }
            }
          ],
          "arguments": {
            "jvm": ["-cp", "${classpath}"],
            "game": ["--username", "${auth_player_name}"]
          }
        }
        """);

    await File.WriteAllTextAsync(
        Path.Combine(childVersionRoot, instance.VersionId + ".json"),
        """
        {
          "id": "fabric-loader-0.16.9-1.21.4",
          "inheritsFrom": "1.21.4",
          "type": "release",
          "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient",
          "libraries": [
            {
              "name": "net.fabricmc:fabric-loader:0.16.9",
              "url": "https://maven.fabricmc.net/"
            }
          ],
          "arguments": {
            "jvm": ["-Dfabric.test=true"],
            "game": ["--fabric-test"]
          }
        }
        """);

    using (var resolved = await new MinecraftVersionMetadataResolver()
               .ResolveAsync(gameRoot, instance.VersionId))
    {
        var metadata = resolved.Document.RootElement;
        Assert(resolved.ClientVersionId == "1.21.4", "Inherited profile should use the parent client JAR.");
        Assert(metadata.GetProperty("mainClass").GetString() == "net.fabricmc.loader.impl.launch.knot.KnotClient",
            "Child main class should override parent main class.");
        Assert(metadata.GetProperty("libraries").GetArrayLength() == 2,
            "Parent and child libraries should both be present.");
        Assert(metadata.GetProperty("arguments").GetProperty("game").GetArrayLength() == 3,
            "Parent and child game arguments should be concatenated.");
        Assert(metadata.GetProperty("javaVersion").GetProperty("majorVersion").GetInt32() == 17,
            "Java version should be inherited from the base Minecraft profile.");
    }

    var javaPath = Path.Combine(root, OperatingSystem.IsWindows() ? "java.exe" : "java");
    await File.WriteAllBytesAsync(javaPath, [4]);
    var java = new JavaInstallation(javaPath, root, "17.0.12", true, "test");
    var account = new LauncherAccount(
        "offline-test",
        "offline",
        "FabricUser",
        Guid.NewGuid().ToString(),
        DateTimeOffset.UtcNow);

    var plan = await new MinecraftLaunchPlanBuilder(paths)
        .BuildAsync(instance, account, [java]);
    Assert(plan.Arguments.Contains("net.fabricmc.loader.impl.launch.knot.KnotClient"),
        "Launch plan should use Fabric KnotClient.");
    Assert(plan.Arguments.Contains("-Dfabric.test=true"),
        "Launch plan should contain Fabric JVM arguments.");
    Assert(plan.Arguments.Contains("--fabric-test"),
        "Launch plan should contain Fabric game arguments.");
    Assert(plan.Arguments.Any(value => value.Contains("fabric-loader", StringComparison.OrdinalIgnoreCase)),
        "Fabric loader library should be present in the classpath argument.");

    var requiredJava = await new MinecraftRuntimeInspector(paths)
        .GetRequiredJavaMajorAsync(instance);
    Assert(requiredJava == 17, "Runtime inspector should inherit Java 17 from the base version.");
}

static async Task RunInheritanceLoopRegressionAsync(string root)
{
    var gameRoot = Path.Combine(root, "loop-game");
    var aRoot = Path.Combine(gameRoot, "versions", "a");
    var bRoot = Path.Combine(gameRoot, "versions", "b");
    Directory.CreateDirectory(aRoot);
    Directory.CreateDirectory(bRoot);
    await File.WriteAllTextAsync(Path.Combine(aRoot, "a.json"), "{\"id\":\"a\",\"inheritsFrom\":\"b\"}");
    await File.WriteAllTextAsync(Path.Combine(bRoot, "b.json"), "{\"id\":\"b\",\"inheritsFrom\":\"a\"}");

    try
    {
        using var _ = await new MinecraftVersionMetadataResolver().ResolveAsync(gameRoot, "a");
        throw new Exception("Inheritance loop should have been rejected.");
    }
    catch (InvalidDataException ex)
    {
        Assert(ex.Message.Contains("loop", StringComparison.OrdinalIgnoreCase),
            "Loop rejection should explain the problem.");
    }
}

static async Task RunFabricMetaRegressionAsync()
{
    var handler = new FabricMetaHandler();
    using var client = new HttpClient(handler);
    var service = new FabricMetaService(client);
    var versions = await service.GetLoaderVersionsAsync("1.21.4");
    Assert(versions.Count == 2, "Fabric Meta loader list should be parsed.");
    Assert(versions[0].Version == "0.16.9" && versions[0].Stable,
        "Stable loader should be preferred.");

    using var profile = await service.GetProfileAsync("1.21.4", "0.16.9");
    Assert(profile.RootElement.GetProperty("inheritsFrom").GetString() == "1.21.4",
        "Fabric profile endpoint should return the requested base version.");
    Assert(handler.RequestedPaths.Contains("/v2/versions/loader/1.21.4"),
        "Loader list endpoint was not requested.");
    Assert(handler.RequestedPaths.Contains("/v2/versions/loader/1.21.4/0.16.9/profile/json"),
        "Profile endpoint was not requested.");
}

static void RunMavenPathRegression()
{
    var path = MavenArtifactPath.FromCoordinate("net.fabricmc:fabric-loader:0.16.9");
    var normalized = path.Replace('\\', '/');
    Assert(normalized == "net/fabricmc/fabric-loader/0.16.9/fabric-loader-0.16.9.jar",
        "Maven path resolution is incorrect.");

    try
    {
        _ = MavenArtifactPath.FromCoordinate("../evil:artifact:1.0");
        throw new Exception("Unsafe Maven coordinate should have been rejected.");
    }
    catch (InvalidDataException)
    {
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);
}

sealed class FabricMetaHandler : HttpMessageHandler
{
    public List<string> RequestedPaths { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        RequestedPaths.Add(path);
        var json = path.EndsWith("/profile/json", StringComparison.Ordinal)
            ? """
              {
                "id":"fabric-loader-0.16.9-1.21.4",
                "inheritsFrom":"1.21.4",
                "mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient",
                "libraries":[{"name":"net.fabricmc:fabric-loader:0.16.9","url":"https://maven.fabricmc.net/"}]
              }
              """
            : """
              [
                {"loader":{"version":"0.16.8","stable":false}},
                {"loader":{"version":"0.16.9","stable":true}}
              ]
              """;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
    }
}
