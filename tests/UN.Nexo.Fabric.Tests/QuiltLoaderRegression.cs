using System.Net;
using System.Security.Cryptography;
using System.Text;
using UN.Nexo.Core.Launching;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Fabric.Tests;

internal static class QuiltLoaderRegression
{
    internal static async Task RunAsync()
    {
        const string minecraftVersion = "1.21.4";
        const string loaderVersion = "0.26.4";
        const string profileId = "quilt-loader-0.26.4-1.21.4";

        var loaderBytes = Encoding.UTF8.GetBytes("quilt-loader");
        var loaderSha1 = Convert.ToHexString(
                SHA1.HashData(loaderBytes))
            .ToLowerInvariant();
        var handler = new QuiltHandler(
            profileId,
            loaderVersion,
            loaderBytes,
            loaderSha1);
        using var client = new HttpClient(handler);

        var meta = new QuiltMetaService(client);
        var versions = await meta.GetLoaderVersionsAsync(
            minecraftVersion);
        Assert(
            versions.Count == 2,
            "Quilt Meta should skip malformed entries while retaining healthy loaders.");
        Assert(
            versions[0].Version == loaderVersion
            && versions[0].Stable,
            "Stable Quilt Loader should be preferred.");
        Assert(
            handler.RequestedPaths.Any(path =>
                path.Equals(
                    "/v3/versions/loader/1.21.4",
                    StringComparison.Ordinal)),
            "Quilt Meta v3 loader endpoint was not used.");

        using (var profile = await meta.GetProfileAsync(
                   minecraftVersion,
                   loaderVersion))
        {
            Assert(
                profile.RootElement.GetProperty("id").GetString()
                == profileId,
                "Quilt launcher profile id was not returned.");
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-quilt-tests-"
            + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new NexoPathService(root);
            paths.EnsureDirectories();
            var sources = new DownloadSourceService();
            var vanilla = new MinecraftVanillaInstallService(
                client,
                paths,
                sources);
            var service = new QuiltInstallService(
                client,
                paths,
                vanilla,
                meta);

            var instance = new GameInstance(
                Guid.NewGuid().ToString("N"),
                "Quilt regression",
                profileId,
                "quilt",
                DateTimeOffset.UtcNow,
                minecraftVersion,
                loaderVersion);
            var instanceRoot = paths.GetInstanceDirectory(instance.Id);
            Directory.CreateDirectory(instanceRoot);

            var baseVersion = new MinecraftVersionInfo(
                minecraftVersion,
                "release",
                "https://piston-meta.mojang.com/version.json",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                string.Empty,
                0);

            await service.PrepareAsync(
                instance,
                baseVersion);

            var profilePath = Path.Combine(
                paths.GetInstanceGameDirectory(instance.Id),
                "versions",
                profileId,
                profileId + ".json");
            Assert(
                File.Exists(profilePath),
                "Quilt preparation must persist the launcher profile.");

            var loaderPath = Path.Combine(
                paths.GetInstanceGameDirectory(instance.Id),
                "libraries",
                "org",
                "quiltmc",
                "quilt-loader",
                loaderVersion,
                $"quilt-loader-{loaderVersion}.jar");
            Assert(
                File.Exists(loaderPath),
                "Quilt preparation must download the verified loader library.");
            Assert(
                (await File.ReadAllBytesAsync(loaderPath))
                    .SequenceEqual(loaderBytes),
                "Quilt loader library bytes changed.");
            Assert(
                File.Exists(
                    Path.Combine(
                        instanceRoot,
                        "install-state.json")),
                "Successful Quilt preparation must publish install state.");

            using (var resolved =
                   await new MinecraftVersionMetadataResolver()
                       .ResolveAsync(
                           paths.GetInstanceGameDirectory(instance.Id),
                           profileId))
            {
                Assert(
                    resolved.ClientVersionId == minecraftVersion,
                    "Quilt profile must resolve the Vanilla client JAR.");
                Assert(
                    resolved.Document.RootElement
                        .GetProperty("mainClass")
                        .GetString()
                    == "org.quiltmc.loader.impl.launch.knot.KnotClient",
                    "Quilt child main class must override Vanilla.");
                Assert(
                    resolved.Document.RootElement
                        .GetProperty("javaVersion")
                        .GetProperty("majorVersion")
                        .GetInt32()
                    == 21,
                    "Quilt must inherit the base Minecraft Java requirement.");
            }

            var javaPath = Path.Combine(
                root,
                OperatingSystem.IsWindows()
                    ? "java.exe"
                    : "java");
            await File.WriteAllBytesAsync(javaPath, [1]);
            var plan = await new MinecraftLaunchPlanBuilder(paths)
                .BuildAsync(
                    instance,
                    new LauncherAccount(
                        "quilt-test",
                        "offline",
                        "QuiltTester",
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
                    "org.quiltmc.loader.impl.launch.knot.KnotClient"),
                "Quilt launch plan must use Quilt KnotClient.");
            Assert(
                plan.Arguments.Any(argument =>
                    argument.Contains(
                        "quilt-loader",
                        StringComparison.OrdinalIgnoreCase)),
                "Quilt loader library must be present in the classpath.");

            var requiredJava =
                await new MinecraftRuntimeInspector(paths)
                    .GetRequiredJavaMajorAsync(instance);
            Assert(
                requiredJava == 21,
                "Runtime inspector must inherit Java 21 for the Quilt instance.");

            await TestLinkedLibraryParentRejectedAsync(
                paths,
                service,
                baseVersion,
                profileId,
                minecraftVersion,
                loaderVersion);

            await TestLinkedInstallStateRejectedAsync(
                paths,
                service,
                baseVersion,
                profileId,
                minecraftVersion,
                loaderVersion);

            await TestLinkedVersionsDirectoryRejectedAsync(
                paths,
                service,
                baseVersion,
                profileId,
                minecraftVersion,
                loaderVersion);

            await TestOversizedLibraryRejectedAsync(
                QuiltLibraryMode.DeclaredOversize,
                "declared oversized");
            await TestOversizedLibraryRejectedAsync(
                QuiltLibraryMode.ChunkedOversize,
                "chunked oversized");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static async Task TestOversizedLibraryRejectedAsync(
        QuiltLibraryMode mode,
        string label)
    {
        const string minecraftVersion = "1.21.4";
        const string loaderVersion = "0.26.4";
        const string profileId =
            "quilt-loader-0.26.4-1.21.4";
        const long maxBytes = 4096;

        var loaderBytes =
            Encoding.UTF8.GetBytes(
                "quilt-loader");
        var loaderSha1 =
            Convert.ToHexString(
                    SHA1.HashData(loaderBytes))
                .ToLowerInvariant();

        var handler =
            new QuiltHandler(
                profileId,
                loaderVersion,
                loaderBytes,
                loaderSha1,
                mode);
        using var client =
            new HttpClient(handler);

        var root =
            Path.Combine(
                Path.GetTempPath(),
                "un-nexo-quilt-size-"
                + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var paths =
                new NexoPathService(root);
            paths.EnsureDirectories();

            var meta =
                new QuiltMetaService(client);
            var vanilla =
                new MinecraftVanillaInstallService(
                    client,
                    paths,
                    new DownloadSourceService());
            var service =
                new QuiltInstallService(
                    client,
                    paths,
                    vanilla,
                    meta,
                    maxLibraryBytes:
                        maxBytes);

            var instance =
                new GameInstance(
                    Guid.NewGuid().ToString("N"),
                    "Quilt size regression",
                    profileId,
                    "quilt",
                    DateTimeOffset.UtcNow,
                    minecraftVersion,
                    loaderVersion);
            var instanceRoot =
                paths.GetInstanceDirectory(
                    instance.Id);
            Directory.CreateDirectory(
                instanceRoot);

            var baseVersion =
                new MinecraftVersionInfo(
                    minecraftVersion,
                    "release",
                    "https://piston-meta.mojang.com/version.json",
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    string.Empty,
                    0);

            try
            {
                await service.PrepareAsync(
                    instance,
                    baseVersion);
                throw new InvalidOperationException(
                    $"{label} Quilt library unexpectedly prepared.");
            }
            catch (InvalidDataException)
            {
            }

            var target =
                Path.Combine(
                    paths.GetInstanceGameDirectory(
                        instance.Id),
                    "libraries",
                    "org",
                    "quiltmc",
                    "quilt-loader",
                    loaderVersion,
                    $"quilt-loader-{loaderVersion}.jar");

            Assert(
                !File.Exists(target),
                $"{label} Quilt library must not publish the final artifact.");
            Assert(
                !File.Exists(target + ".part"),
                $"{label} Quilt library must clean its trusted .part file.");
            Assert(
                !File.Exists(
                    Path.Combine(
                        instanceRoot,
                        "install-state.json")),
                $"{label} Quilt library must not publish prepared state.");
            Assert(
                handler.RequestedPaths.Any(path =>
                    path.EndsWith(
                        "/quilt-loader.jar",
                        StringComparison.Ordinal)),
                $"{label} Quilt regression did not reach the loader library request.");
        }
        finally
        {
            TryDeleteTree(root);
        }
    }

    private static async Task TestLinkedLibraryParentRejectedAsync(
        NexoPathService paths,
        QuiltInstallService service,
        MinecraftVersionInfo baseVersion,
        string profileId,
        string minecraftVersion,
        string loaderVersion)
    {
        var instance = new GameInstance(
            Guid.NewGuid().ToString("N"),
            "Quilt linked-library regression",
            profileId,
            "quilt",
            DateTimeOffset.UtcNow,
            minecraftVersion,
            loaderVersion);
        var instanceRoot = paths.GetInstanceDirectory(instance.Id);
        Directory.CreateDirectory(instanceRoot);

        var librariesRoot = Path.Combine(
            paths.GetInstanceGameDirectory(instance.Id),
            "libraries");
        Directory.CreateDirectory(librariesRoot);

        var externalRoot = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-quilt-linked-library-"
            + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(externalRoot);
        var sentinel = Path.Combine(externalRoot, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "keep");

        var linkedOrg = Path.Combine(librariesRoot, "org");
        var linked = false;
        try
        {
            linked = TryCreateDirectoryLink(linkedOrg, externalRoot);
            if (!linked)
                return;

            try
            {
                await service.PrepareAsync(instance, baseVersion);
                throw new InvalidOperationException(
                    "Linked Quilt Maven parent unexpectedly accepted a library download.");
            }
            catch (InvalidDataException)
            {
            }

            Assert(
                File.Exists(sentinel),
                "Rejecting a linked Quilt Maven parent must not modify its external target.");
            Assert(
                !Directory.EnumerateFiles(
                    externalRoot,
                    "*",
                    SearchOption.AllDirectories)
                    .Any(path => !Path.GetFullPath(path)
                        .Equals(
                            Path.GetFullPath(sentinel),
                            OperatingSystem.IsWindows()
                                ? StringComparison.OrdinalIgnoreCase
                                : StringComparison.Ordinal)),
                "Quilt library preparation must not write through a linked Maven parent.");
        }
        finally
        {
            if (linked)
                TryDeleteDirectoryLink(linkedOrg);
            TryDeleteTree(instanceRoot);
            TryDeleteTree(externalRoot);
        }
    }

    private static async Task TestLinkedVersionsDirectoryRejectedAsync(
        NexoPathService paths,
        QuiltInstallService service,
        MinecraftVersionInfo baseVersion,
        string profileId,
        string minecraftVersion,
        string loaderVersion)
    {
        var instance =
            new GameInstance(
                Guid.NewGuid().ToString("N"),
                "Quilt linked versions regression",
                profileId,
                "quilt",
                DateTimeOffset.UtcNow,
                minecraftVersion,
                loaderVersion);
        var instanceRoot =
            paths.GetInstanceDirectory(instance.Id);
        Directory.CreateDirectory(instanceRoot);
        var gameRoot =
            paths.GetInstanceGameDirectory(instance.Id);
        Directory.CreateDirectory(gameRoot);

        var externalRoot =
            Path.Combine(
                Path.GetTempPath(),
                "un-nexo-quilt-profile-external-"
                + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(externalRoot);

        var linkedVersions =
            Path.Combine(
                gameRoot,
                "versions");
        var linked = false;
        try
        {
            linked =
                TryCreateDirectoryLink(
                    linkedVersions,
                    externalRoot);
            if (!linked)
                return;

            try
            {
                await service.PrepareAsync(
                    instance,
                    baseVersion);
                throw new InvalidOperationException(
                    "Linked Quilt versions directory unexpectedly accepted profile preparation.");
            }
            catch (InvalidDataException)
            {
            }

            Assert(
                !Directory.EnumerateFileSystemEntries(
                    externalRoot)
                    .Any(),
                "Quilt profile preparation must not write through a linked versions directory.");
        }
        finally
        {
            if (linked)
                TryDeleteDirectoryLink(linkedVersions);
            TryDeleteTree(instanceRoot);
            TryDeleteTree(externalRoot);
        }
    }

    private static async Task TestLinkedInstallStateRejectedAsync(
        NexoPathService paths,
        QuiltInstallService service,
        MinecraftVersionInfo baseVersion,
        string profileId,
        string minecraftVersion,
        string loaderVersion)
    {
        var instance = new GameInstance(
            Guid.NewGuid().ToString("N"),
            "Quilt linked-state regression",
            profileId,
            "quilt",
            DateTimeOffset.UtcNow,
            minecraftVersion,
            loaderVersion);
        var instanceRoot =
            paths.GetInstanceDirectory(instance.Id);
        Directory.CreateDirectory(instanceRoot);

        var externalRoot =
            Path.Combine(
                Path.GetTempPath(),
                "un-nexo-quilt-linked-state-"
                + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(externalRoot);
        var sentinel =
            Path.Combine(
                externalRoot,
                "outside.json");
        var sentinelBytes =
            Encoding.UTF8.GetBytes(
                "{\"outside\":true}");
        await File.WriteAllBytesAsync(
            sentinel,
            sentinelBytes);

        var statePath =
            Path.Combine(
                instanceRoot,
                "install-state.json");
        var linked = false;
        try
        {
            linked =
                TryCreateFileLink(
                    statePath,
                    sentinel);
            if (!linked)
                return;

            try
            {
                await service.PrepareAsync(
                    instance,
                    baseVersion);
                throw new InvalidOperationException(
                    "Linked Quilt install state unexpectedly produced a rollback snapshot.");
            }
            catch (InvalidDataException)
            {
            }

            Assert(
                (await File.ReadAllBytesAsync(sentinel))
                    .SequenceEqual(sentinelBytes),
                "Rejecting linked Quilt install state must preserve the external target.");
        }
        finally
        {
            if (linked)
                TryDeleteFileLink(statePath);
            TryDeleteTree(instanceRoot);
            TryDeleteTree(externalRoot);
        }
    }

    private static bool TryCreateFileLink(
        string linkPath,
        string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(
                linkPath,
                targetPath);
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

    private static void TryDeleteFileLink(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
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

    private enum QuiltLibraryMode
    {
        Valid,
        DeclaredOversize,
        ChunkedOversize
    }

    private sealed class QuiltHandler(
        string profileId,
        string loaderVersion,
        byte[] loaderBytes,
        string loaderSha1,
        QuiltLibraryMode libraryMode =
            QuiltLibraryMode.Valid) : HttpMessageHandler
    {
        public List<string> RequestedPaths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri
                ?? throw new InvalidOperationException(
                    "Missing request URI.");
            RequestedPaths.Add(uri.AbsolutePath);

            if (uri.Host.Equals(
                    "meta.quiltmc.org",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (uri.AbsolutePath.EndsWith(
                        "/profile/json",
                        StringComparison.Ordinal))
                {
                    var profile =
                        "{"
                        + "\"id\":\"" + profileId + "\","
                        + "\"inheritsFrom\":\"1.21.4\","
                        + "\"mainClass\":\"org.quiltmc.loader.impl.launch.knot.KnotClient\","
                        + "\"libraries\":[{"
                        + "\"name\":\"org.quiltmc:quilt-loader:" + loaderVersion + "\","
                        + "\"downloads\":{\"artifact\":{"
                        + "\"path\":\"org/quiltmc/quilt-loader/" + loaderVersion
                        + "/quilt-loader-" + loaderVersion + ".jar\","
                        + "\"url\":\"https://maven.quiltmc.org/quilt-loader.jar\","
                        + "\"sha1\":\"" + loaderSha1 + "\"}}}],"
                        + "\"arguments\":{\"jvm\":[\"-Dquilt.test=true\"],"
                        + "\"game\":[\"--quilt-test\"]}"
                        + "}";
                    return Json(profile);
                }

                return Json(
                    "["
                    + "{\"loader\":{\"version\":\"0.25.0\",\"stable\":false}},"
                    + "123,"
                    + "{\"loader\":[]},"
                    + "{\"loader\":{\"version\":123}},"
                    + "{\"loader\":{\"version\":\"" + loaderVersion + "\",\"stable\":true}}"
                    + "]");
            }

            if (uri.Host.Equals(
                    "piston-meta.mojang.com",
                    StringComparison.OrdinalIgnoreCase))
            {
                return Json(
                    "{"
                    + "\"id\":\"1.21.4\","
                    + "\"mainClass\":\"net.minecraft.client.main.Main\","
                    + "\"javaVersion\":{\"majorVersion\":21},"
                    + "\"libraries\":[],"
                    + "\"arguments\":{"
                    + "\"jvm\":[\"-cp\",\"${classpath}\"],"
                    + "\"game\":[\"--username\",\"${auth_player_name}\"]},"
                    + "\"downloads\":{\"client\":{"
                    + "\"url\":\"https://piston-data.mojang.com/client.jar\","
                    + "\"size\":6}},"
                    + "\"assetIndex\":{"
                    + "\"id\":\"quilt-assets\","
                    + "\"url\":\"https://launchermeta.mojang.com/quilt-assets.json\"}"
                    + "}");
            }

            if (uri.Host.Equals(
                    "piston-data.mojang.com",
                    StringComparison.OrdinalIgnoreCase))
            {
                return Bytes(Encoding.UTF8.GetBytes("client"));
            }

            if (uri.Host.Equals(
                    "launchermeta.mojang.com",
                    StringComparison.OrdinalIgnoreCase))
            {
                return Json("{\"objects\":{}}");
            }

            if (uri.Host.Equals(
                    "maven.quiltmc.org",
                    StringComparison.OrdinalIgnoreCase))
            {
                return libraryMode switch
                {
                    QuiltLibraryMode.Valid =>
                        Bytes(loaderBytes),
                    QuiltLibraryMode.DeclaredOversize =>
                        DeclaredOversize(
                            loaderBytes),
                    QuiltLibraryMode.ChunkedOversize =>
                        Stream(
                            new FiniteUnseekableStream(
                                8192)),
                    _ => throw new ArgumentOutOfRangeException()
                };
            }

            return Task.FromResult(
                new HttpResponseMessage(
                    HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json(string json)
            => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        json,
                        Encoding.UTF8,
                        "application/json")
                });

        private static Task<HttpResponseMessage> Bytes(byte[] bytes)
            => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(bytes)
                });

        private static Task<HttpResponseMessage> DeclaredOversize(
            byte[] bytes)
        {
            var content =
                new ByteArrayContent(bytes);
            content.Headers.ContentLength =
                8192;
            return Task.FromResult(
                new HttpResponseMessage(
                    HttpStatusCode.OK)
                {
                    Content = content
                });
        }

        private static Task<HttpResponseMessage> Stream(
            Stream stream)
            => Task.FromResult(
                new HttpResponseMessage(
                    HttpStatusCode.OK)
                {
                    Content =
                        new StreamContent(stream)
                });
    }

    private sealed class FiniteUnseekableStream(
        long length) : Stream
    {
        private long _remaining =
            length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length
            => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(
            byte[] buffer,
            int offset,
            int count)
            => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_remaining <= 0)
                return ValueTask.FromResult(0);

            var count =
                (int)Math.Min(
                    _remaining,
                    buffer.Length);
            buffer.Span[..count]
                .Fill(0x51);
            _remaining -= count;
            return ValueTask.FromResult(count);
        }

        public override void Flush() { }
        public override long Seek(
            long offset,
            SeekOrigin origin)
            => throw new NotSupportedException();
        public override void SetLength(long value)
            => throw new NotSupportedException();
        public override void Write(
            byte[] buffer,
            int offset,
            int count)
            => throw new NotSupportedException();
    }
}
