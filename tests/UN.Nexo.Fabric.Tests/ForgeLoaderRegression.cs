using System.Net;
using System.Security.Cryptography;
using System.Text;
using UN.Nexo.Core.Launching;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Fabric.Tests;

internal static class ForgeLoaderRegression
{
    internal static async Task RunAsync()
    {
        const string minecraftVersion = "1.21.4";
        const string forgeVersion = "54.0.12";
        const string launchId = "1.21.4-forge-54.0.12";

        var installerBytes =
            Encoding.UTF8.GetBytes("verified-forge-installer");
        var installerSha1 =
            Convert.ToHexString(SHA1.HashData(installerBytes))
                .ToLowerInvariant();

        var handler =
            new ForgeHandler(
                installerBytes,
                installerSha1);
        using var client =
            new HttpClient(handler);

        var meta =
            new ForgeMetaService(client);
        var versions =
            await meta.GetVersionsAsync(minecraftVersion);

        Assert(
            versions.Count == 2,
            "Forge promotions should expose recommended and latest versions.");
        Assert(
            versions[0].Version == "54.0.10"
            && versions[0].IsRecommended,
            "Forge recommended promotion should be preferred.");
        Assert(
            versions[1].Version == forgeVersion
            && versions[1].IsLatest,
            "Forge latest promotion should be retained.");
        Assert(
            ForgeMetaService.GetLaunchVersionId(
                minecraftVersion,
                forgeVersion) == launchId,
            "Forge launch version id was constructed incorrectly.");

        var root =
            Path.Combine(
                Path.GetTempPath(),
                "un-nexo-forge-tests-"
                + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths =
                new NexoPathService(root);
            paths.EnsureDirectories();
            var sources =
                new DownloadSourceService();
            var vanilla =
                new MinecraftVanillaInstallService(
                    client,
                    paths,
                    sources);

            var instance =
                new GameInstance(
                    Guid.NewGuid().ToString("N"),
                    "Forge regression",
                    launchId,
                    "forge",
                    DateTimeOffset.UtcNow,
                    minecraftVersion,
                    forgeVersion);
            Directory.CreateDirectory(
                paths.GetInstanceDirectory(instance.Id));

            var javaPath =
                Path.Combine(
                    root,
                    OperatingSystem.IsWindows()
                        ? "java.exe"
                        : "java");
            await File.WriteAllBytesAsync(
                javaPath,
                [1]);

            var processCalls = 0;
            async Task<ForgeInstallerProcessResult> RunInstallerAsync(
                System.Diagnostics.ProcessStartInfo startInfo,
                CancellationToken cancellationToken)
            {
                processCalls++;
                Assert(
                    startInfo.ArgumentList.Count == 4,
                    "Forge installer must receive exactly the expected CLI arguments.");
                Assert(
                    startInfo.ArgumentList[0] == "-jar",
                    "Forge installer must be launched with -jar.");
                Assert(
                    startInfo.ArgumentList[2] == "--installClient",
                    "Forge installer must use installClient mode.");
                Assert(
                    startInfo.ArgumentList[3] == startInfo.WorkingDirectory,
                    "Forge installer target must be the isolated instance game directory.");

                var gameRoot =
                    startInfo.WorkingDirectory;
                var versionRoot =
                    Path.Combine(
                        gameRoot,
                        "versions",
                        launchId);
                var libraryRelative =
                    Path.Combine(
                        "net",
                        "minecraftforge",
                        "forge-test",
                        "1.0",
                        "forge-test-1.0.jar");
                var libraryPath =
                    Path.Combine(
                        gameRoot,
                        "libraries",
                        libraryRelative);
                Directory.CreateDirectory(versionRoot);
                Directory.CreateDirectory(
                    Path.GetDirectoryName(libraryPath)!);
                await File.WriteAllBytesAsync(
                    libraryPath,
                    [7],
                    cancellationToken);

                await File.WriteAllTextAsync(
                    Path.Combine(
                        versionRoot,
                        launchId + ".json"),
                    """
                    {
                      "id":"1.21.4-forge-54.0.12",
                      "inheritsFrom":"1.21.4",
                      "mainClass":"cpw.mods.bootstraplauncher.BootstrapLauncher",
                      "libraries":[
                        {
                          "name":"net.minecraftforge:forge-test:1.0",
                          "downloads":{
                            "artifact":{
                              "path":"net/minecraftforge/forge-test/1.0/forge-test-1.0.jar",
                              "size":1
                            }
                          }
                        }
                      ],
                      "arguments":{
                        "jvm":["-Dforge.test=true"],
                        "game":["--forge-test"]
                      }
                    }
                    """,
                    cancellationToken);

                return new ForgeInstallerProcessResult(
                    0,
                    "success",
                    string.Empty);
            }

            var service =
                new ForgeInstallService(
                    client,
                    paths,
                    vanilla,
                    processRunner: RunInstallerAsync,
                    javaProvisioner: (
                        major,
                        progress,
                        cancellationToken) =>
                    {
                        Assert(
                            major == 21,
                            "Forge installer Java must follow the base Minecraft Java requirement.");
                        return Task.FromResult(
                            new JavaInstallation(
                                javaPath,
                                root,
                                "21.0.8",
                                true,
                                "test"));
                    });

            var baseVersion =
                new MinecraftVersionInfo(
                    minecraftVersion,
                    "release",
                    "https://piston-meta.mojang.com/version.json",
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    handler.MetadataSha1,
                    0);

            await service.PrepareAsync(
                instance,
                baseVersion);

            Assert(
                processCalls == 1,
                "Forge installer process should run once.");
            Assert(
                handler.InstallerRequests == 1,
                "Verified Forge installer should be downloaded once.");
            Assert(
                handler.ChecksumRequests == 1,
                "Forge installer SHA-1 sidecar should be fetched once.");

            var installState =
                Path.Combine(
                    paths.GetInstanceDirectory(instance.Id),
                    "install-state.json");
            Assert(
                File.Exists(installState),
                "Successful Forge preparation must publish install state.");

            var cachedInstaller =
                Path.Combine(
                    paths.GetInstanceDirectory(instance.Id),
                    "loader-installers",
                    "forge",
                    $"forge-{minecraftVersion}-{forgeVersion}-installer.jar");
            Assert(
                File.Exists(cachedInstaller),
                "Verified Forge installer should be cached inside the instance.");
            Assert(
                (await File.ReadAllBytesAsync(cachedInstaller))
                    .SequenceEqual(installerBytes),
                "Cached Forge installer bytes changed.");

            var plan =
                await new MinecraftLaunchPlanBuilder(paths)
                    .BuildAsync(
                        instance,
                        new LauncherAccount(
                            "forge-test",
                            "offline",
                            "ForgeTester",
                            Guid.NewGuid().ToString(),
                            DateTimeOffset.UtcNow),
                        [
                            new JavaInstallation(
                                javaPath,
                                root,
                                "21.0.8",
                                true,
                                "test")
                        ]);

            Assert(
                plan.Arguments.Contains(
                    "cpw.mods.bootstraplauncher.BootstrapLauncher"),
                "Forge launch plan must use the installer-generated main class.");
            Assert(
                plan.Arguments.Contains(
                    "-Dforge.test=true"),
                "Forge launch plan must include child JVM arguments.");
            Assert(
                plan.Arguments.Contains(
                    "--forge-test"),
                "Forge launch plan must include child game arguments.");
            Assert(
                plan.Arguments.Any(argument =>
                    argument.Contains(
                        "forge-test",
                        StringComparison.OrdinalIgnoreCase)),
                "Forge installer-generated library must be present in the classpath.");

            var requiredJava =
                await new MinecraftRuntimeInspector(paths)
                    .GetRequiredJavaMajorAsync(instance);
            Assert(
                requiredJava == 21,
                "Forge instance must inherit Java 21 from the base Minecraft profile.");

            await service.PrepareAsync(
                instance,
                baseVersion);
            Assert(
                handler.InstallerRequests == 1,
                "A verified cached Forge installer should not be downloaded again.");
            Assert(
                processCalls == 2,
                "Repairing Forge should still rerun the official installer processors.");

            await TestLinkedInstallerTreeRejectedAsync(
                client,
                paths,
                vanilla,
                baseVersion,
                javaPath,
                root,
                minecraftVersion,
                forgeVersion,
                launchId);
        }
        finally
        {
            try
            {
                Directory.Delete(
                    root,
                    recursive: true);
            }
            catch
            {
            }
        }
    }

    private static async Task TestLinkedInstallerTreeRejectedAsync(
        HttpClient client,
        NexoPathService paths,
        MinecraftVanillaInstallService vanilla,
        MinecraftVersionInfo baseVersion,
        string javaPath,
        string testRoot,
        string minecraftVersion,
        string forgeVersion,
        string launchId)
    {
        var externalRoot = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-forge-linked-installer-"
            + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(externalRoot);
        var sentinel = Path.Combine(externalRoot, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "keep");

        var probe = Path.Combine(
            testRoot,
            "forge-link-probe-" + Guid.NewGuid().ToString("N"));
        if (!TryCreateDirectoryLink(probe, externalRoot))
        {
            TryDeleteTree(externalRoot);
            return;
        }
        TryDeleteDirectoryLink(probe);

        var instance = new GameInstance(
            Guid.NewGuid().ToString("N"),
            "Forge linked installer tree",
            launchId,
            "forge",
            DateTimeOffset.UtcNow,
            minecraftVersion,
            forgeVersion);
        var instanceRoot = paths.GetInstanceDirectory(instance.Id);
        Directory.CreateDirectory(instanceRoot);

        var linkedPath = Path.Combine(
            paths.GetInstanceGameDirectory(instance.Id),
            "libraries",
            "net");
        var processCalls = 0;

        var service = new ForgeInstallService(
            client,
            paths,
            vanilla,
            processRunner: (startInfo, cancellationToken) =>
            {
                processCalls++;
                return Task.FromResult(
                    new ForgeInstallerProcessResult(
                        0,
                        "unexpected process execution",
                        string.Empty));
            },
            javaProvisioner: (major, progress, cancellationToken) =>
            {
                var parent = Path.GetDirectoryName(linkedPath)
                    ?? throw new InvalidOperationException(
                        "Linked Forge regression path has no parent.");
                Directory.CreateDirectory(parent);
                Assert(
                    TryCreateDirectoryLink(linkedPath, externalRoot),
                    "Forge regression could not create the linked Maven directory after Vanilla preparation.");
                return Task.FromResult(
                    new JavaInstallation(
                        javaPath,
                        testRoot,
                        "21.0.8",
                        true,
                        "test"));
            });

        try
        {
            try
            {
                await service.PrepareAsync(instance, baseVersion);
                throw new InvalidOperationException(
                    "Forge preparation unexpectedly accepted a linked installer-owned library directory.");
            }
            catch (InvalidDataException)
            {
            }

            Assert(
                processCalls == 0,
                "Forge installer process must not start after an installer-owned tree becomes linked.");
            Assert(
                File.Exists(sentinel),
                "Rejecting the linked Forge installer tree must preserve the external sentinel.");
            Assert(
                Directory.EnumerateFileSystemEntries(externalRoot)
                    .All(path => Path.GetFileName(path) == "keep.txt"),
                "Forge preparation must not write through the linked installer-owned tree.");
        }
        finally
        {
            TryDeleteDirectoryLink(linkedPath);
            TryDeleteTree(instanceRoot);
            TryDeleteTree(externalRoot);
        }
    }

    private static bool TryCreateDirectoryLink(
        string linkPath,
        string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException
            or IOException
            or PlatformNotSupportedException
            or NotSupportedException)
        {
            return false;
        }
    }

    private static void TryDeleteDirectoryLink(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path);
        }
        catch
        {
        }
    }

    private static void TryDeleteTree(string path)
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

    private static void Assert(
        bool condition,
        string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class ForgeHandler(
        byte[] installerBytes,
        string installerSha1) : HttpMessageHandler
    {
        private const string Metadata =
            """
            {
              "id":"1.21.4",
              "mainClass":"net.minecraft.client.main.Main",
              "javaVersion":{"majorVersion":21},
              "libraries":[],
              "arguments":{
                "jvm":["-cp","${classpath}"],
                "game":["--username","${auth_player_name}"]
              },
              "downloads":{
                "client":{
                  "url":"https://piston-data.mojang.com/client.jar",
                  "size":6
                }
              },
              "assetIndex":{
                "id":"forge-assets",
                "url":"https://launchermeta.mojang.com/forge-assets.json"
              }
            }
            """;

        public int InstallerRequests { get; private set; }
        public int ChecksumRequests { get; private set; }
        public string MetadataSha1
            => Convert.ToHexString(
                    SHA1.HashData(
                        Encoding.UTF8.GetBytes(Metadata)))
                .ToLowerInvariant();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri =
                request.RequestUri
                ?? throw new InvalidOperationException(
                    "Missing request URI.");

            if (uri.Host.Equals(
                    "files.minecraftforge.net",
                    StringComparison.OrdinalIgnoreCase))
            {
                return Json(
                    """
                    {
                      "promos":{
                        "1.21.4-recommended":"54.0.10",
                        "1.21.4-latest":"54.0.12"
                      }
                    }
                    """);
            }

            if (uri.Host.Equals(
                    "maven.minecraftforge.net",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (uri.AbsolutePath.EndsWith(
                        ".sha1",
                        StringComparison.Ordinal))
                {
                    ChecksumRequests++;
                    return Task.FromResult(
                        new HttpResponseMessage(
                            HttpStatusCode.OK)
                        {
                            Content =
                                new StringContent(
                                    installerSha1,
                                    Encoding.ASCII,
                                    "text/plain")
                        });
                }

                if (uri.AbsolutePath.EndsWith(
                        "-installer.jar",
                        StringComparison.Ordinal))
                {
                    InstallerRequests++;
                    return Bytes(installerBytes);
                }

                return Task.FromResult(
                    new HttpResponseMessage(
                        HttpStatusCode.NotFound));
            }

            if (uri.Host.Equals(
                    "piston-meta.mojang.com",
                    StringComparison.OrdinalIgnoreCase))
            {
                return Json(Metadata);
            }

            if (uri.Host.Equals(
                    "piston-data.mojang.com",
                    StringComparison.OrdinalIgnoreCase))
            {
                return Bytes(
                    Encoding.UTF8.GetBytes(
                        "client"));
            }

            if (uri.Host.Equals(
                    "launchermeta.mojang.com",
                    StringComparison.OrdinalIgnoreCase))
            {
                return Json(
                    "{\"objects\":{}}");
            }

            return Task.FromResult(
                new HttpResponseMessage(
                    HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json(
            string json)
            => Task.FromResult(
                new HttpResponseMessage(
                    HttpStatusCode.OK)
                {
                    Content =
                        new StringContent(
                            json,
                            Encoding.UTF8,
                            "application/json")
                });

        private static Task<HttpResponseMessage> Bytes(
            byte[] bytes)
            => Task.FromResult(
                new HttpResponseMessage(
                    HttpStatusCode.OK)
                {
                    Content =
                        new ByteArrayContent(bytes)
                });
    }
}
