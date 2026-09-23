using System.Net;
using System.Security.Cryptography;
using System.Text;
using UN.Nexo.Core.Launching;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Fabric.Tests;

internal static class NeoForgeLoaderRegression
{
    internal static async Task RunAsync()
    {
        const string minecraftVersion = "1.21.4";
        const string neoForgeVersion = "21.4.156";
        const string launchId = "neoforge-21.4.156";

        Assert(
            NeoForgeMetaService.IsCompatibleWithMinecraft("1.21.1", "21.1.26"),
            "NeoForge 21.1.x should map to Minecraft 1.21.1.");
        Assert(
            NeoForgeMetaService.IsCompatibleWithMinecraft("26.2", "26.2.0.88"),
            "NeoForge 26.2.0.x should map to Minecraft 26.2.");
        Assert(
            !NeoForgeMetaService.IsCompatibleWithMinecraft(minecraftVersion, "21.5.10"),
            "NeoForge for another Minecraft patch must not be treated as compatible.");

        var installerBytes = Encoding.UTF8.GetBytes("verified-neoforge-installer");
        var installerSha1 = Convert.ToHexString(SHA1.HashData(installerBytes)).ToLowerInvariant();
        var handler = new NeoForgeHandler(installerBytes, installerSha1);
        using var client = new HttpClient(handler);

        var meta = new NeoForgeMetaService(client);
        var versions = await meta.GetVersionsAsync(minecraftVersion);
        Assert(versions.Count == 3,
            "NeoForge metadata should retain only versions compatible with the selected Minecraft release.");
        Assert(versions[0].Version == neoForgeVersion && versions[0].IsLatest,
            "Newest compatible stable NeoForge build should be selected first.");
        Assert(versions[1].Version == "21.4.155",
            "Compatible NeoForge versions should be sorted newest-first.");
        Assert(versions[2].Version == "21.4.154-beta" && versions[2].IsPrerelease,
            "Compatible prerelease should be retained behind newer stable builds.");
        Assert(
            NeoForgeMetaService.GetLaunchVersionId(minecraftVersion, neoForgeVersion) == launchId,
            "NeoForge launch id must match the installer profile naming.");
        Assert(
            NeoForgeMetaService.GetInstallerUrl(minecraftVersion, neoForgeVersion)
                .EndsWith("/21.4.156/neoforge-21.4.156-installer.jar", StringComparison.Ordinal),
            "NeoForge installer URL must use the official Maven artifact naming without embedding the Minecraft version.");

        var root = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-neoforge-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var paths = new NexoPathService(root);
            paths.EnsureDirectories();
            var vanilla = new MinecraftVanillaInstallService(
                client,
                paths,
                new DownloadSourceService());

            var instance = new GameInstance(
                Guid.NewGuid().ToString("N"),
                "NeoForge regression",
                launchId,
                "neoforge",
                DateTimeOffset.UtcNow,
                minecraftVersion,
                neoForgeVersion);
            Directory.CreateDirectory(paths.GetInstanceDirectory(instance.Id));

            var javaPath = Path.Combine(
                root,
                OperatingSystem.IsWindows() ? "java.exe" : "java");
            await File.WriteAllBytesAsync(javaPath, [1]);

            var processCalls = 0;
            async Task<NeoForgeInstallerProcessResult> RunInstallerAsync(
                System.Diagnostics.ProcessStartInfo startInfo,
                CancellationToken cancellationToken)
            {
                processCalls++;
                Assert(startInfo.ArgumentList.Count == 4,
                    "NeoForge installer must receive the expected CLI arguments.");
                Assert(
                    startInfo.ArgumentList[0] == "-jar"
                    && startInfo.ArgumentList[2] == "--installClient",
                    "NeoForge installer must run in installClient mode.");
                Assert(startInfo.ArgumentList[3] == startInfo.WorkingDirectory,
                    "NeoForge installer target must be the isolated instance game directory.");

                var gameRoot = startInfo.WorkingDirectory;
                var versionRoot = Path.Combine(gameRoot, "versions", launchId);
                var libraryRelative = Path.Combine(
                    "net", "neoforged", "neoforge-test", "1.0", "neoforge-test-1.0.jar");
                var libraryPath = Path.Combine(gameRoot, "libraries", libraryRelative);

                Directory.CreateDirectory(versionRoot);
                Directory.CreateDirectory(Path.GetDirectoryName(libraryPath)!);
                await File.WriteAllBytesAsync(libraryPath, [7], cancellationToken);

                await File.WriteAllTextAsync(
                    Path.Combine(versionRoot, launchId + ".json"),
                    """
                    {
                      "id":"neoforge-21.4.156",
                      "inheritsFrom":"1.21.4",
                      "mainClass":"cpw.mods.bootstraplauncher.BootstrapLauncher",
                      "libraries":[
                        {
                          "name":"net.neoforged:neoforge-test:1.0",
                          "downloads":{
                            "artifact":{
                              "path":"net/neoforged/neoforge-test/1.0/neoforge-test-1.0.jar",
                              "size":1
                            }
                          }
                        }
                      ],
                      "arguments":{
                        "jvm":["-Dneoforge.test=true"],
                        "game":["--neoforge-test"]
                      }
                    }
                    """,
                    cancellationToken);

                return new NeoForgeInstallerProcessResult(0, "success", string.Empty);
            }

            var service = new NeoForgeInstallService(
                client,
                paths,
                vanilla,
                processRunner: RunInstallerAsync,
                javaProvisioner: (major, progress, cancellationToken) =>
                {
                    Assert(major == 21,
                        "NeoForge installer Java must follow the base Minecraft Java requirement.");
                    return Task.FromResult(
                        new JavaInstallation(javaPath, root, "21.0.8", true, "test"));
                });

            var baseVersion = new MinecraftVersionInfo(
                minecraftVersion,
                "release",
                "https://piston-meta.mojang.com/version.json",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                handler.MetadataSha1,
                0);

            await service.PrepareAsync(instance, baseVersion);

            Assert(processCalls == 1,
                "NeoForge installer process should run once.");
            Assert(handler.InstallerRequests == 1,
                "Verified NeoForge installer should be downloaded once.");
            Assert(handler.ChecksumRequests == 1,
                "NeoForge installer SHA-1 sidecar should be fetched once.");

            var installState = Path.Combine(
                paths.GetInstanceDirectory(instance.Id),
                "install-state.json");
            Assert(File.Exists(installState),
                "Successful NeoForge preparation must publish install state.");

            var cachedInstaller = Path.Combine(
                paths.GetInstanceDirectory(instance.Id),
                "loader-installers",
                "neoforge",
                "neoforge-21.4.156-installer.jar");
            Assert(File.Exists(cachedInstaller),
                "Verified NeoForge installer should be cached inside the instance.");
            Assert(
                (await File.ReadAllBytesAsync(cachedInstaller)).SequenceEqual(installerBytes),
                "Cached NeoForge installer bytes changed.");

            var account = new LauncherAccount(
                "neoforge-test",
                "offline",
                "NeoForgeTester",
                Guid.NewGuid().ToString(),
                DateTimeOffset.UtcNow);
            var java = new JavaInstallation(javaPath, root, "21.0.8", true, "test");
            var plan = await new MinecraftLaunchPlanBuilder(paths)
                .BuildAsync(instance, account, [java]);

            Assert(plan.Arguments.Contains("cpw.mods.bootstraplauncher.BootstrapLauncher"),
                "NeoForge launch plan must use the installer-generated main class.");
            Assert(plan.Arguments.Contains("-Dneoforge.test=true"),
                "NeoForge launch plan must include child JVM arguments.");
            Assert(plan.Arguments.Contains("--neoforge-test"),
                "NeoForge launch plan must include child game arguments.");
            Assert(
                plan.Arguments.Any(argument =>
                    argument.Contains("neoforge-test", StringComparison.OrdinalIgnoreCase)),
                "NeoForge installer-generated library must be present in the classpath.");

            var requiredJava = await new MinecraftRuntimeInspector(paths)
                .GetRequiredJavaMajorAsync(instance);
            Assert(requiredJava == 21,
                "NeoForge instance must inherit Java 21 from the base Minecraft profile.");

            await service.PrepareAsync(instance, baseVersion);
            Assert(handler.InstallerRequests == 1,
                "A verified cached NeoForge installer should not be downloaded again.");
            Assert(processCalls == 2,
                "Repairing NeoForge should rerun the official installer processors.");

            await TestLinkedInstallerTreeRejectedAsync(
                client,
                paths,
                vanilla,
                baseVersion,
                javaPath,
                root,
                minecraftVersion,
                neoForgeVersion,
                launchId);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
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
        string neoForgeVersion,
        string launchId)
    {
        var externalRoot = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-neoforge-linked-installer-"
            + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(externalRoot);
        var sentinel = Path.Combine(externalRoot, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "keep");

        var probe = Path.Combine(
            testRoot,
            "neoforge-link-probe-" + Guid.NewGuid().ToString("N"));
        if (!TryCreateDirectoryLink(probe, externalRoot))
        {
            TryDeleteTree(externalRoot);
            return;
        }
        TryDeleteDirectoryLink(probe);

        var instance = new GameInstance(
            Guid.NewGuid().ToString("N"),
            "NeoForge linked installer tree",
            launchId,
            "neoforge",
            DateTimeOffset.UtcNow,
            minecraftVersion,
            neoForgeVersion);
        var instanceRoot = paths.GetInstanceDirectory(instance.Id);
        Directory.CreateDirectory(instanceRoot);

        var linkedPath = Path.Combine(
            paths.GetInstanceGameDirectory(instance.Id),
            "libraries",
            "net");
        var processCalls = 0;

        var service = new NeoForgeInstallService(
            client,
            paths,
            vanilla,
            processRunner: (startInfo, cancellationToken) =>
            {
                processCalls++;
                return Task.FromResult(
                    new NeoForgeInstallerProcessResult(
                        0,
                        "unexpected process execution",
                        string.Empty));
            },
            javaProvisioner: (major, progress, cancellationToken) =>
            {
                var parent = Path.GetDirectoryName(linkedPath)
                    ?? throw new InvalidOperationException(
                        "Linked NeoForge regression path has no parent.");
                Directory.CreateDirectory(parent);
                Assert(
                    TryCreateDirectoryLink(linkedPath, externalRoot),
                    "NeoForge regression could not create the linked Maven directory after Vanilla preparation.");
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
                    "NeoForge preparation unexpectedly accepted a linked installer-owned library directory.");
            }
            catch (InvalidDataException)
            {
            }

            Assert(
                processCalls == 0,
                "NeoForge installer process must not start after an installer-owned tree becomes linked.");
            Assert(
                File.Exists(sentinel),
                "Rejecting the linked NeoForge installer tree must preserve the external sentinel.");
            Assert(
                Directory.EnumerateFileSystemEntries(externalRoot)
                    .All(path => Path.GetFileName(path) == "keep.txt"),
                "NeoForge preparation must not write through the linked installer-owned tree.");
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

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class NeoForgeHandler(
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
                "id":"neoforge-assets",
                "url":"https://launchermeta.mojang.com/neoforge-assets.json"
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
            var uri = request.RequestUri
                ?? throw new InvalidOperationException("Missing request URI.");

            if (uri.Host.Equals("maven.neoforged.net", StringComparison.OrdinalIgnoreCase))
            {
                if (uri.AbsolutePath.EndsWith("/maven-metadata.xml", StringComparison.Ordinal))
                {
                    return Text(
                        """
                        <metadata>
                          <groupId>net.neoforged</groupId>
                          <artifactId>neoforge</artifactId>
                          <versioning>
                            <versions>
                              <version>21.4.154-beta</version>
                              <version>21.4.155</version>
                              <version>21.5.10</version>
                              <version>21.4.156</version>
                              <version>26.2.0.88</version>
                            </versions>
                          </versioning>
                        </metadata>
                        """,
                        "application/xml");
                }

                if (uri.AbsolutePath.EndsWith(".sha1", StringComparison.Ordinal))
                {
                    ChecksumRequests++;
                    return Text(installerSha1, "text/plain");
                }

                if (uri.AbsolutePath.EndsWith("-installer.jar", StringComparison.Ordinal))
                {
                    InstallerRequests++;
                    return Bytes(installerBytes);
                }
            }

            if (uri.Host.Equals("piston-meta.mojang.com", StringComparison.OrdinalIgnoreCase))
            {
                return Text(
                    Metadata,
                    "application/json");
            }

            if (uri.Host.Equals("piston-data.mojang.com", StringComparison.OrdinalIgnoreCase))
                return Bytes(Encoding.UTF8.GetBytes("client"));

            if (uri.Host.Equals("launchermeta.mojang.com", StringComparison.OrdinalIgnoreCase))
                return Text("{\"objects\":{}}", "application/json");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Text(string value, string mediaType)
            => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(value, Encoding.UTF8, mediaType)
                });

        private static Task<HttpResponseMessage> Bytes(byte[] bytes)
            => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(bytes)
                });
    }
}
