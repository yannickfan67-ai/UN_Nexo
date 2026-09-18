using UN.Nexo.Core.Launching;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class LaunchMetadataRegression
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-launch-shape-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new NexoPathService(root);
            var instance = new GameInstance(
                "launch-shape-instance",
                "Launch shape test",
                "launch-shape",
                "vanilla",
                DateTimeOffset.UtcNow);
            var gameRoot = paths.GetInstanceGameDirectory(instance.Id);
            var versionRoot = Path.Combine(gameRoot, "versions", instance.VersionId);
            var assetsRoot = Path.Combine(gameRoot, "assets");
            Directory.CreateDirectory(versionRoot);
            Directory.CreateDirectory(Path.Combine(assetsRoot, "indexes"));
            await File.WriteAllBytesAsync(
                Path.Combine(versionRoot, instance.VersionId + ".jar"),
                [1]);

            var indexPath = Path.Combine(assetsRoot, "indexes", "shape-assets.json");
            var metadataPath = Path.Combine(versionRoot, instance.VersionId + ".json");
            var javaPath = Path.Combine(root, OperatingSystem.IsWindows() ? "java.exe" : "java");
            await File.WriteAllBytesAsync(javaPath, [1]);

            var java = new JavaInstallation(javaPath, root, "21.0.1", true, "test");
            var account = new LauncherAccount(
                "shape-account",
                "offline",
                "ShapeUser",
                Guid.NewGuid().ToString(),
                DateTimeOffset.UtcNow);
            var builder = new MinecraftLaunchPlanBuilder(paths);

            const string validMetadata = """
                {
                  "id":"launch-shape",
                  "type":"release",
                  "javaVersion":{"majorVersion":21},
                  "downloads":{"client":{"size":1}},
                  "assetIndex":{"id":"shape-assets"},
                  "libraries":[],
                  "mainClass":"net.minecraft.client.main.Main",
                  "arguments":{"jvm":["-cp","__CLASSPATH__"],"game":[]}
                }
                """;
            const string validIndex = """
                {"objects":{}}
                """;
            var baseMetadata = validMetadata.Replace(
                "__CLASSPATH__",
                "$" + "{classpath}",
                StringComparison.Ordinal);

            async Task ExpectInvalidAsync(string metadata, string index, string label)
            {
                await File.WriteAllTextAsync(metadataPath, metadata);
                await File.WriteAllTextAsync(indexPath, index);
                try
                {
                    await builder.BuildAsync(instance, account, [java]);
                    throw new Exception($"Malformed launch fixture '{label}' should be rejected.");
                }
                catch (InvalidDataException)
                {
                }
            }

            var malformedMetadata = new (string Label, string Json)[]
            {
                (
                    "java major string",
                    baseMetadata.Replace(
                        "\"javaVersion\":{\"majorVersion\":21}",
                        "\"javaVersion\":{\"majorVersion\":\"21\"}",
                        StringComparison.Ordinal)
                ),
                (
                    "libraries object",
                    baseMetadata.Replace("\"libraries\":[]", "\"libraries\":{}", StringComparison.Ordinal)
                ),
                (
                    "numeric library",
                    baseMetadata.Replace("\"libraries\":[]", "\"libraries\":[123]", StringComparison.Ordinal)
                ),
                (
                    "numeric artifact path",
                    baseMetadata.Replace(
                        "\"libraries\":[]",
                        "\"libraries\":[{\"downloads\":{\"artifact\":{\"path\":123}}}]",
                        StringComparison.Ordinal)
                ),
                (
                    "game arguments object",
                    baseMetadata.Replace("\"game\":[]", "\"game\":{}", StringComparison.Ordinal)
                ),
                (
                    "numeric game argument",
                    baseMetadata.Replace("\"game\":[]", "\"game\":[123]", StringComparison.Ordinal)
                ),
                (
                    "numeric conditional value",
                    baseMetadata.Replace(
                        "\"game\":[]",
                        "\"game\":[{\"value\":123}]",
                        StringComparison.Ordinal)
                ),
                (
                    "numeric main class",
                    baseMetadata.Replace(
                        "\"mainClass\":\"net.minecraft.client.main.Main\"",
                        "\"mainClass\":123",
                        StringComparison.Ordinal)
                ),
                (
                    "numeric logging file id",
                    baseMetadata.Replace(
                        "\"arguments\":",
                        "\"logging\":{\"client\":{\"argument\":\"-Dlog=__LOGPATH__\",\"file\":{\"id\":123}}},\"arguments\":",
                        StringComparison.Ordinal)
                        .Replace("__LOGPATH__", "$" + "{path}", StringComparison.Ordinal)
                ),
                (
                    "numeric type",
                    baseMetadata.Replace("\"type\":\"release\"", "\"type\":123", StringComparison.Ordinal)
                ),
                (
                    "numeric asset index id",
                    baseMetadata.Replace(
                        "\"assetIndex\":{\"id\":\"shape-assets\"}",
                        "\"assetIndex\":{\"id\":123}",
                        StringComparison.Ordinal)
                ),
                (
                    "string client size",
                    baseMetadata.Replace(
                        "\"client\":{\"size\":1}",
                        "\"client\":{\"size\":\"1\"}",
                        StringComparison.Ordinal)
                )
            };

            foreach (var fixture in malformedMetadata)
                await ExpectInvalidAsync(fixture.Json, validIndex, fixture.Label);

            var malformedIndexes = new (string Label, string Json)[]
            {
                ("objects array", "{\"objects\":[]}"),
                ("numeric asset entry", "{\"objects\":{\"x\":123}}"),
                ("numeric asset hash", "{\"objects\":{\"x\":{\"hash\":123}}}"),
                ("invalid asset hash", "{\"objects\":{\"x\":{\"hash\":\"a\"}}}"),
                ("string virtual flag", "{\"objects\":{},\"virtual\":\"yes\"}"),
                ("numeric resource-map flag", "{\"objects\":{},\"map_to_resources\":1}")
            };

            foreach (var fixture in malformedIndexes)
                await ExpectInvalidAsync(baseMetadata, fixture.Json, fixture.Label);

            await File.WriteAllTextAsync(metadataPath, baseMetadata);
            await File.WriteAllTextAsync(indexPath, validIndex);
            var plan = await builder.BuildAsync(instance, account, [java]);
            if (!plan.Arguments.Contains("net.minecraft.client.main.Main", StringComparer.Ordinal))
                throw new Exception("Valid launch metadata should still build a launch plan.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
