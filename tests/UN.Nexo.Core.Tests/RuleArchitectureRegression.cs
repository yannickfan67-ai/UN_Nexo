using System.Runtime.InteropServices;
using System.Text.Json;
using UN.Nexo.Core.Launching;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class RuleArchitectureRegression
{
    internal static async Task RunAsync()
    {
        TestFeatureRules();
        TestArchitectureRules();
        TestQuickPlayFeatureContext();
        await TestArm64LaunchPlanAsync();
    }

    private static void TestFeatureRules()
    {
        using var requiredTrue = JsonDocument.Parse(
            """
            {
              "rules":[
                {"action":"allow","features":{"has_custom_resolution":true}}
              ]
            }
            """);
        Assert(!MinecraftRules.Allows(requiredTrue.RootElement),
            "Missing launcher feature must evaluate as false.");
        Assert(MinecraftRules.Allows(
                requiredTrue.RootElement,
                new Dictionary<string, bool>
                {
                    ["has_custom_resolution"] = true
                }),
            "Explicit true feature context should satisfy a true feature rule.");
        Assert(!MinecraftRules.Allows(
                requiredTrue.RootElement,
                new Dictionary<string, bool>
                {
                    ["has_custom_resolution"] = false
                }),
            "Explicit false feature must not satisfy a true feature rule.");

        using var requiredFalse = JsonDocument.Parse(
            """
            {
              "rules":[
                {"action":"allow","features":{"has_custom_resolution":false}}
              ]
            }
            """);
        Assert(MinecraftRules.Allows(requiredFalse.RootElement),
            "Missing feature should satisfy a rule explicitly requiring false.");
        Assert(!MinecraftRules.Allows(
                requiredFalse.RootElement,
                new Dictionary<string, bool>
                {
                    ["has_custom_resolution"] = true
                }),
            "True context must not satisfy a false feature rule.");

        using var malformed = JsonDocument.Parse(
            """
            {
              "rules":[
                {"action":"allow","features":{"has_custom_resolution":"yes"}}
              ]
            }
            """);
        Expect<InvalidDataException>(
            () => MinecraftRules.Allows(malformed.RootElement),
            "Non-boolean feature metadata must fail explicitly.");
    }

    private static void TestArchitectureRules()
    {
        Assert(
            MinecraftRules.ArchitectureNameFor(Architecture.X64) == "x86_64",
            "x64 rule architecture mapping");
        Assert(
            MinecraftRules.ArchitectureNameFor(Architecture.X86) == "x86",
            "x86 rule architecture mapping");
        Assert(
            MinecraftRules.ArchitectureNameFor(Architecture.Arm64) == "aarch64",
            "ARM64 rule architecture mapping");

        using var armOnly = JsonDocument.Parse(
            """
            {
              "rules":[
                {"action":"allow","os":{"name":"linux","arch":"aarch64"}}
              ]
            }
            """);
        Assert(
            MinecraftRules.Allows(
                armOnly.RootElement,
                "linux",
                "aarch64",
                "6.8.0"),
            "aarch64 environment should satisfy aarch64 metadata.");
        Assert(
            !MinecraftRules.Allows(
                armOnly.RootElement,
                "linux",
                "x86_64",
                "6.8.0"),
            "x86_64 environment must not satisfy aarch64 metadata.");
    }

    private static void TestQuickPlayFeatureContext()
    {
        using var root = JsonDocument.Parse(
            """
            {
              "arguments":{
                "game":[
                  {
                    "rules":[
                      {
                        "action":"allow",
                        "features":{"is_quick_play_multiplayer":true}
                      }
                    ],
                    "value":[
                      "--quickPlayMultiplayer",
                      "${quickPlayMultiplayer}"
                    ]
                  }
                ]
              }
            }
            """);
        Assert(
            MinecraftServerLaunchDecorator.SupportsQuickPlayMultiplayer(
                root.RootElement),
            "Quick Play detection should satisfy the explicit multiplayer feature context.");
    }

    private static async Task TestArm64LaunchPlanAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-arm64-launch-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new NexoPathService(root);
            var instance = new GameInstance(
                Guid.NewGuid().ToString("N"),
                "ARM64 fixture",
                "arm64-fixture",
                "vanilla",
                DateTimeOffset.UtcNow);
            var identity = TestMicrosoftIdentity.Create("ArmTester");
            var account = identity.Account;

            var instanceRoot = paths.GetInstanceDirectory(instance.Id);
            var gameRoot = paths.GetInstanceGameDirectory(instance.Id);
            var versionRoot = Path.Combine(gameRoot, "versions", instance.VersionId);
            var assetsRoot = Path.Combine(gameRoot, "assets");
            var librariesRoot = Path.Combine(gameRoot, "libraries");
            Directory.CreateDirectory(versionRoot);
            Directory.CreateDirectory(Path.Combine(assetsRoot, "indexes"));
            Directory.CreateDirectory(librariesRoot);

            await File.WriteAllTextAsync(
                Path.Combine(instanceRoot, "install-state.json"),
                "{}");
            await File.WriteAllBytesAsync(
                Path.Combine(versionRoot, instance.VersionId + ".jar"),
                [1]);
            await File.WriteAllTextAsync(
                Path.Combine(assetsRoot, "indexes", "arm-assets.json"),
                "{\"objects\":{}}");

            const string armRelative =
                "com/example/arm-only/1.0/arm-only-1.0.jar";
            const string x64Relative =
                "com/example/x64-only/1.0/x64-only-1.0.jar";
            var armLibrary = Path.Combine(
                librariesRoot,
                armRelative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(armLibrary)!);
            await File.WriteAllBytesAsync(armLibrary, [2]);

            var metadata =
                """
                {
                  "id":"arm64-fixture",
                  "type":"release",
                  "javaVersion":{"majorVersion":21},
                  "downloads":{"client":{}},
                  "assetIndex":{"id":"arm-assets"},
                  "libraries":[
                    {
                      "name":"com.example:arm-only:1.0",
                      "rules":[
                        {"action":"allow","os":{"arch":"aarch64"}}
                      ],
                      "downloads":{
                        "artifact":{
                          "path":"com/example/arm-only/1.0/arm-only-1.0.jar",
                          "size":1
                        }
                      }
                    },
                    {
                      "name":"com.example:x64-only:1.0",
                      "rules":[
                        {"action":"allow","os":{"arch":"x86_64"}}
                      ],
                      "downloads":{
                        "artifact":{
                          "path":"com/example/x64-only/1.0/x64-only-1.0.jar",
                          "size":1
                        }
                      }
                    }
                  ],
                  "mainClass":"net.minecraft.client.main.Main",
                  "arguments":{
                    "jvm":["-cp","${classpath}"],
                    "game":["--username","${auth_player_name}"]
                  }
                }
                """;
            await File.WriteAllTextAsync(
                Path.Combine(versionRoot, instance.VersionId + ".json"),
                metadata);

            var java21 = Path.Combine(root, "java21-arm64");
            await File.WriteAllTextAsync(java21, "test");
            var builder = new MinecraftLaunchPlanBuilder(paths);
            var plan = await builder.BuildForArchitectureAsync(
                instance,
                account,
                [new JavaInstallation(
                    java21,
                    root,
                    "21.0.8",
                    true,
                    "manual ARM64 fixture")],
                credentials: identity.Credentials,
                architecture: Architecture.Arm64);

            Assert(plan.JavaPath == java21,
                "ARM64 plan should use the supplied compatible 64-bit Java.");
            Assert(plan.Arguments.Any(argument =>
                    argument.Contains(
                        "arm-only-1.0.jar",
                        StringComparison.Ordinal)),
                "ARM64 classpath should include the aarch64-guarded library.");
            Assert(!plan.Arguments.Any(argument =>
                    argument.Contains(
                        "x64-only-1.0.jar",
                        StringComparison.Ordinal)),
                "ARM64 classpath must exclude the x86_64-guarded library.");
            Assert(!File.Exists(Path.Combine(
                    librariesRoot,
                    x64Relative.Replace('/', Path.DirectorySeparatorChar))),
                "Regression fixture intentionally provides no x64-only artifact.");

            try
            {
                _ = await builder.BuildForArchitectureAsync(
                    instance,
                    account,
                    [],
                    credentials: identity.Credentials,
                    architecture: Architecture.Arm64);
                throw new Exception(
                    "ARM64 launch without compatible Java unexpectedly succeeded.");
            }
            catch (PlatformNotSupportedException ex)
            {
                Assert(
                    ex.Message.Contains(
                        "Automatic Java acquisition currently supports x64 only",
                        StringComparison.Ordinal),
                    "ARM64 missing-Java failure should explain the managed-runtime limitation.");
            }

            try
            {
                _ = await builder.BuildForArchitectureAsync(
                    instance,
                    account,
                    [new JavaInstallation(
                        java21,
                        root,
                        "21.0.8",
                        true,
                        "fixture")],
                    credentials: identity.Credentials,
                    architecture: Architecture.X86);
                throw new Exception(
                    "Unsupported x86 launch unexpectedly succeeded.");
            }
            catch (PlatformNotSupportedException)
            {
            }
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

    private static void Expect<TException>(
        Action action,
        string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new Exception(
            message + " Expected " + typeof(TException).Name + ".");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
