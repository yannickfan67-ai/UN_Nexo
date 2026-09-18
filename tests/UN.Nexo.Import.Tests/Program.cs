using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Import.Tests;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static async Task<int> Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "UN_Nexo-Import-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source", ".minecraft");
        var data = Path.Combine(root, "nexo-data");
        Directory.CreateDirectory(source);
        try
        {
            await CreateCompleteVanillaAsync(source, "1.21.4");
            await CreateFabricProfileAsync(source, "fabric-loader-0.16.0-1.21.4", "1.21.4");
            Directory.CreateDirectory(Path.Combine(source, "saves", "Regression World"));
            await File.WriteAllTextAsync(Path.Combine(source, "saves", "Regression World", "level.dat"), "world-data");
            Directory.CreateDirectory(Path.Combine(source, "mods"));
            await File.WriteAllTextAsync(Path.Combine(source, "mods", "sodium.jar"), "fake-mod");
            Directory.CreateDirectory(Path.Combine(source, "config"));
            await File.WriteAllTextAsync(Path.Combine(source, "config", "sodium-options.json"), "{}");
            Directory.CreateDirectory(Path.Combine(source, "resourcepacks", "pack-a"));
            await File.WriteAllTextAsync(Path.Combine(source, "resourcepacks", "pack-a", "pack.mcmeta"), "{}");
            await File.WriteAllTextAsync(Path.Combine(source, "options.txt"), "fov:90");
            await File.WriteAllTextAsync(Path.Combine(source, "launcher_accounts.json"), "TOP-SECRET-LAUNCHER-TOKEN");

            var paths = new NexoPathService(data);
            var importer = new GameDirectoryImportService(paths);
            var store = new InstanceStoreService(paths);

            var preview = await importer.ScanAsync(source);
            Require(preview.WorldCount == 1, "Scan should count worlds.");
            Require(preview.ModCount == 1, "Scan should count mods.");
            Require(preview.ResourcePackCount == 1, "Scan should count resource packs.");
            var vanilla = preview.Versions.Single(item => item.VersionId == "1.21.4");
            var fabric = preview.Versions.Single(item => item.VersionId == "fabric-loader-0.16.0-1.21.4");
            Require(vanilla.Loader == "vanilla" && vanilla.IsSupportedLoader, "Vanilla should be detected as launchable.");
            Require(fabric.Loader == "fabric" && !fabric.IsSupportedLoader, "Fabric should be recognized but require preparation before launch.");
            Require(fabric.BaseVersionId == "1.21.4", "Fabric preview should preserve the inherited base version.");
            Require(fabric.LoaderVersion == "0.16.0", "Fabric preview should detect the loader version.");
            Require(preview.Warnings.Any(item => item.Contains("Fabric", StringComparison.OrdinalIgnoreCase)
                                                && item.Contains("prepar", StringComparison.OrdinalIgnoreCase)),
                "Fabric scan warning should direct the user to preparation.");
            Require(preview.Warnings.All(item => !item.Contains("#21", StringComparison.Ordinal)),
                "Fabric scan warning must not claim launch support is waiting on #21.");

            await TestCompleteVanillaImportAsync(importer, paths, preview, vanilla, source);
            await TestFabricImportAsync(importer, paths, preview, fabric, source);
            await TestIncompleteVanillaReusesFilesAsync(root, paths);
            await TestDuplicateNameAsync(importer, store, preview, vanilla, source);
            await TestCancelledImportLeavesNoInstanceAsync(importer, store, preview, vanilla, source);
            await TestUnsafeVersionPathMetadataAsync(root, paths);
            await TestSymlinkedVersionMetadataAsync(root, paths);

            Require(await File.ReadAllTextAsync(Path.Combine(source, "options.txt")) == "fov:90", "Source settings changed during import.");
            Require(await File.ReadAllTextAsync(Path.Combine(source, "mods", "sodium.jar")) == "fake-mod", "Source mod changed during import.");
            Require(await File.ReadAllTextAsync(Path.Combine(source, "launcher_accounts.json")) == "TOP-SECRET-LAUNCHER-TOKEN",
                "Source launcher account file changed during import.");

            Console.WriteLine("PASS game directory import regressions");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL game directory import regressions");
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task TestCompleteVanillaImportAsync(
        GameDirectoryImportService importer,
        NexoPathService paths,
        GameDirectoryImportPreview preview,
        ImportVersionCandidate vanilla,
        string source)
    {
        var result = await importer.ImportAsync(preview, vanilla, "Imported Vanilla");
        Require(result.PreparedFromExistingFiles, "Complete verified Vanilla import should be marked prepared.");
        Require(result.Instance.Loader == "vanilla", "Imported Vanilla loader mismatch.");
        var instanceRoot = paths.GetInstanceDirectory(result.Instance.Id);
        var gameRoot = paths.GetInstanceGameDirectory(result.Instance.Id);
        Require(File.Exists(Path.Combine(instanceRoot, "install-state.json")), "Prepared import should have install-state.json.");
        Require(File.Exists(Path.Combine(gameRoot, "versions", "1.21.4", "1.21.4.jar")), "Client jar should be copied.");
        Require(File.Exists(Path.Combine(gameRoot, "saves", "Regression World", "level.dat")), "World should be preserved.");
        Require(File.Exists(Path.Combine(gameRoot, "mods", "sodium.jar")), "Mods should be preserved even for selected Vanilla profile.");
        Require(File.Exists(Path.Combine(gameRoot, "resourcepacks", "pack-a", "pack.mcmeta")), "Resource packs should be preserved.");
        Require(File.Exists(Path.Combine(gameRoot, "options.txt")), "Game settings should be preserved.");
        Require(!File.Exists(Path.Combine(gameRoot, "launcher_accounts.json")), "Launcher credentials must not be imported into the instance.");
        Require(await File.ReadAllTextAsync(Path.Combine(source, "saves", "Regression World", "level.dat")) == "world-data",
            "Source world must stay untouched.");
    }

    private static async Task TestFabricImportAsync(
        GameDirectoryImportService importer,
        NexoPathService paths,
        GameDirectoryImportPreview preview,
        ImportVersionCandidate fabric,
        string source)
    {
        var result = await importer.ImportAsync(preview, fabric, "Imported Fabric");
        Require(!result.PreparedFromExistingFiles, "Fabric import must not masquerade as prepared Vanilla.");
        Require(result.Instance.Loader == "fabric", "Fabric loader should be retained in instance metadata.");
        Require(result.Instance.BaseVersionId == "1.21.4", "Imported Fabric instance should retain BaseVersionId.");
        Require(result.Instance.LoaderVersion == "0.16.0", "Imported Fabric instance should retain LoaderVersion.");
        var gameRoot = paths.GetInstanceGameDirectory(result.Instance.Id);
        Require(File.Exists(Path.Combine(gameRoot, "mods", "sodium.jar")), "Fabric mods should be copied.");
        Require(File.Exists(Path.Combine(gameRoot, "config", "sodium-options.json")), "Fabric config should be copied.");
        Require(File.Exists(Path.Combine(gameRoot, "versions", fabric.VersionId, fabric.VersionId + ".json")),
            "Fabric profile metadata should be copied.");
        Require(File.Exists(Path.Combine(gameRoot, "versions", "1.21.4", "1.21.4.jar")),
            "Fabric base Vanilla version should be copied for later loader support.");
        Require(result.Warnings.Any(item =>
                item.Contains("Fabric", StringComparison.OrdinalIgnoreCase)
                && item.Contains("prepar", StringComparison.OrdinalIgnoreCase)),
            "Fabric import should direct the user to Fabric preparation.");
        Require(result.Warnings.All(item => !item.Contains("#21", StringComparison.Ordinal)),
            "Fabric import must not claim launch support is blocked on #21.");
        Require(await File.ReadAllTextAsync(Path.Combine(source, "mods", "sodium.jar")) == "fake-mod",
            "Fabric source must remain unchanged.");
    }

    private static async Task TestIncompleteVanillaReusesFilesAsync(string root, NexoPathService paths)
    {
        var source = Path.Combine(root, "source-incomplete", ".minecraft");
        Directory.CreateDirectory(source);
        await CreateCompleteVanillaAsync(source, "1.20.1");
        var metadataPath = Path.Combine(source, "versions", "1.20.1", "1.20.1.json");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(metadataPath));
        var libraryPath = document.RootElement.GetProperty("libraries")[0].GetProperty("downloads").GetProperty("artifact").GetProperty("path").GetString()!;
        File.Delete(Path.Combine(source, "libraries", libraryPath.Replace('/', Path.DirectorySeparatorChar)));

        var importer = new GameDirectoryImportService(paths);
        var preview = await importer.ScanAsync(source);
        var candidate = preview.Versions.Single(item => item.VersionId == "1.20.1");
        var result = await importer.ImportAsync(preview, candidate, "Incomplete Vanilla");
        Require(!result.PreparedFromExistingFiles, "Incomplete Vanilla should require repair/download.");
        var gameRoot = paths.GetInstanceGameDirectory(result.Instance.Id);
        Require(File.Exists(Path.Combine(gameRoot, "versions", "1.20.1", "1.20.1.jar")),
            "Valid existing client jar should still be reused/copied.");
        Require(!File.Exists(Path.Combine(paths.GetInstanceDirectory(result.Instance.Id), "install-state.json")),
            "Incomplete import should not claim a prepared state.");
    }

    private static async Task TestDuplicateNameAsync(
        GameDirectoryImportService importer,
        InstanceStoreService store,
        GameDirectoryImportPreview preview,
        ImportVersionCandidate vanilla,
        string source)
    {
        await store.CreateAsync("Duplicate Import", "1.21.4");
        var before = await SnapshotAsync(source);
        try
        {
            await importer.ImportAsync(preview, vanilla, "Duplicate Import");
            throw new InvalidOperationException("Duplicate instance name unexpectedly imported.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
        }
        Require(before == await SnapshotAsync(source), "Duplicate-name failure changed the source directory.");
    }

    private static async Task TestCancelledImportLeavesNoInstanceAsync(
        GameDirectoryImportService importer,
        InstanceStoreService store,
        GameDirectoryImportPreview preview,
        ImportVersionCandidate vanilla,
        string source)
    {
        var beforeInstances = await store.GetAllAsync();
        var sourceBefore = await SnapshotAsync(source);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await importer.ImportAsync(preview, vanilla, "Cancelled Import", cancellation.Token);
            throw new InvalidOperationException("Cancelled import unexpectedly succeeded.");
        }
        catch (OperationCanceledException)
        {
        }
        var afterInstances = await store.GetAllAsync();
        Require(afterInstances.Count == beforeInstances.Count, "Cancelled import should not publish a partial instance.");
        Require(afterInstances.All(item => item.Name != "Cancelled Import"), "Cancelled import should not appear in instance list.");
        Require(sourceBefore == await SnapshotAsync(source), "Cancelled import changed source files.");
    }

    private static async Task TestUnsafeVersionPathMetadataAsync(
        string root,
        NexoPathService paths)
    {
        var source = Path.Combine(root, "unsafe-version-source", ".minecraft");
        var versionsRoot = Path.Combine(source, "versions");
        Directory.CreateDirectory(versionsRoot);

        await WriteSimpleProfileAsync(
            versionsRoot,
            "healthy-profile",
            "healthy-profile",
            null);

        var unsafeValues = new[]
        {
            "../outside",
            "../../outside",
            "a/b",
            @"a\b",
            ".",
            "..",
            Path.GetFullPath(Path.Combine(root, "rooted-outside"))
        };

        for (var index = 0; index < unsafeValues.Length; index++)
        {
            var folder = $"bad-inherits-{index:D2}";
            await WriteSimpleProfileAsync(
                versionsRoot,
                folder,
                folder,
                unsafeValues[index],
                fabric: true);

            var idFolder = $"bad-id-{index:D2}";
            await WriteSimpleProfileAsync(
                versionsRoot,
                idFolder,
                unsafeValues[index],
                null);
        }

        var outside = Path.GetFullPath(Path.Combine(versionsRoot, "..", "..", "outside"));
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "outside-sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "do-not-import");

        var importer = new GameDirectoryImportService(paths);
        var preview = await importer.ScanAsync(source);

        Require(
            preview.Versions.Any(item => item.VersionId == "healthy-profile"),
            "Healthy sibling profile should remain discoverable.");
        Require(
            preview.Versions.All(item =>
                !item.VersionId.StartsWith("bad-inherits-", StringComparison.Ordinal)
                && !item.VersionId.StartsWith("bad-id-", StringComparison.Ordinal)),
            "Unsafe id/inheritsFrom profiles must not be returned as import candidates.");
        Require(
            preview.Warnings.Any(item =>
                item.Contains("safe filesystem component", StringComparison.OrdinalIgnoreCase)
                || item.Contains("Skipped", StringComparison.OrdinalIgnoreCase)),
            "Unsafe version metadata should produce an actionable scan warning.");
        Require(await File.ReadAllTextAsync(sentinel) == "do-not-import",
            "Unsafe inheritsFrom scanning must not touch data outside versions/.");

        var malicious = new ImportVersionCandidate(
            "../../outside",
            "fabric",
            "../../outside",
            Path.Combine(versionsRoot, "healthy-profile", "healthy-profile.json"),
            false,
            false,
            "malicious traversal fixture",
            "0.0.0");
        try
        {
            await importer.ImportAsync(preview, malicious, "Unsafe traversal import");
            throw new InvalidOperationException("Unsafe traversal candidate unexpectedly imported.");
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("changed", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("disappeared", StringComparison.OrdinalIgnoreCase))
        {
        }

        Require(await File.ReadAllTextAsync(sentinel) == "do-not-import",
            "Rejected traversal import must not copy or mutate the outside sentinel.");
    }

    private static async Task TestSymlinkedVersionMetadataAsync(
        string root,
        NexoPathService paths)
    {
        var source = Path.Combine(root, "symlink-version-source", ".minecraft");
        var versionsRoot = Path.Combine(source, "versions");
        Directory.CreateDirectory(versionsRoot);
        await WriteSimpleProfileAsync(
            versionsRoot,
            "regular-profile",
            "regular-profile",
            null);

        var externalRoot = Path.Combine(root, "external-version-metadata");
        Directory.CreateDirectory(externalRoot);
        var externalPreferred = Path.Combine(externalRoot, "preferred.json");
        var externalFallback = Path.Combine(externalRoot, "fallback.json");
        await File.WriteAllTextAsync(
            externalPreferred,
            JsonSerializer.Serialize(new { id = "linked-preferred", mainClass = "example.Main", libraries = Array.Empty<object>() }, Json));
        await File.WriteAllTextAsync(
            externalFallback,
            JsonSerializer.Serialize(new { id = "linked-fallback", mainClass = "example.Main", libraries = Array.Empty<object>() }, Json));

        var preferredDirectory = Path.Combine(versionsRoot, "linked-preferred");
        var fallbackDirectory = Path.Combine(versionsRoot, "linked-fallback");
        Directory.CreateDirectory(preferredDirectory);
        Directory.CreateDirectory(fallbackDirectory);

        var linksCreated = TryCreateFileSymlink(
            Path.Combine(preferredDirectory, "linked-preferred.json"),
            externalPreferred)
            && TryCreateFileSymlink(
                Path.Combine(fallbackDirectory, "other.json"),
                externalFallback);
        if (!linksCreated)
        {
            Console.WriteLine("SKIP import metadata symlink regression: runner cannot create file symlinks.");
            return;
        }

        var importer = new GameDirectoryImportService(paths);
        var preview = await importer.ScanAsync(source);
        Require(
            preview.Versions.Any(item => item.VersionId == "regular-profile"),
            "Regular metadata file should remain importable next to rejected symlinks.");
        Require(
            preview.Versions.All(item =>
                item.VersionId is not "linked-preferred" and not "linked-fallback"),
            "Preferred and fallback metadata symlinks must not become import candidates.");
        Require(
            preview.Warnings.Count(item =>
                item.Contains("Symbolic links", StringComparison.OrdinalIgnoreCase)
                || item.Contains("reparse", StringComparison.OrdinalIgnoreCase)) >= 2,
            "Both symlinked metadata files should produce scan warnings.");

        var regular = preview.Versions.Single(item => item.VersionId == "regular-profile");
        var regularMetadata = regular.MetadataPath;
        File.Delete(regularMetadata);
        var externalReplacement = Path.Combine(externalRoot, "replacement.json");
        await File.WriteAllTextAsync(
            externalReplacement,
            JsonSerializer.Serialize(new { id = "regular-profile", mainClass = "example.Main", libraries = Array.Empty<object>() }, Json));
        Require(
            TryCreateFileSymlink(regularMetadata, externalReplacement),
            "TOCTOU regression requires replacing the approved metadata file with a symlink.");

        try
        {
            await importer.ImportAsync(preview, regular, "Symlink swap import");
            throw new InvalidOperationException("Import unexpectedly accepted metadata replaced by a symlink.");
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("changed", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("disappeared", StringComparison.OrdinalIgnoreCase))
        {
        }

        Require(
            (File.GetAttributes(regularMetadata) & FileAttributes.ReparsePoint) != 0,
            "Rejected import should leave the source symlink untouched.");
    }

    private static async Task WriteSimpleProfileAsync(
        string versionsRoot,
        string folder,
        string id,
        string? inheritsFrom,
        bool fabric = false)
    {
        var directory = Path.Combine(versionsRoot, folder);
        Directory.CreateDirectory(directory);
        object metadata = inheritsFrom is null
            ? new
            {
                id,
                mainClass = "example.Main",
                libraries = Array.Empty<object>()
            }
            : new
            {
                id,
                inheritsFrom,
                mainClass = fabric
                    ? "net.fabricmc.loader.impl.launch.knot.KnotClient"
                    : "example.Main",
                libraries = fabric
                    ? new[] { new { name = "net.fabricmc:fabric-loader:0.16.0" } }
                    : Array.Empty<object>()
            };
        await File.WriteAllTextAsync(
            Path.Combine(directory, folder + ".json"),
            JsonSerializer.Serialize(metadata, Json));
    }

    private static bool TryCreateFileSymlink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return (File.GetAttributes(linkPath) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (
            ex is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException
            or NotSupportedException)
        {
            return false;
        }
    }

    private static async Task CreateCompleteVanillaAsync(string gameRoot, string versionId)
    {
        var versionRoot = Path.Combine(gameRoot, "versions", versionId);
        var librariesRoot = Path.Combine(gameRoot, "libraries");
        var assetsRoot = Path.Combine(gameRoot, "assets");
        Directory.CreateDirectory(versionRoot);
        Directory.CreateDirectory(librariesRoot);
        Directory.CreateDirectory(Path.Combine(assetsRoot, "indexes"));
        Directory.CreateDirectory(Path.Combine(assetsRoot, "objects"));

        var clientPath = Path.Combine(versionRoot, versionId + ".jar");
        await File.WriteAllTextAsync(clientPath, "client-" + versionId);
        var client = await DescriptorAsync(clientPath);

        const string libraryRelative = "com/example/regression/1.0/regression-1.0.jar";
        var libraryPath = Path.Combine(librariesRoot, libraryRelative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(libraryPath)!);
        await File.WriteAllTextAsync(libraryPath, "library-data");
        var library = await DescriptorAsync(libraryPath);

        var objectBytes = Encoding.UTF8.GetBytes("asset-object-" + versionId);
        var objectHash = Sha1(objectBytes);
        var objectPath = Path.Combine(assetsRoot, "objects", objectHash[..2], objectHash);
        Directory.CreateDirectory(Path.GetDirectoryName(objectPath)!);
        await File.WriteAllBytesAsync(objectPath, objectBytes);

        var indexPath = Path.Combine(assetsRoot, "indexes", versionId + ".json");
        await File.WriteAllTextAsync(indexPath, JsonSerializer.Serialize(new
        {
            objects = new Dictionary<string, object>
            {
                ["minecraft/lang/en_us.json"] = new { hash = objectHash, size = objectBytes.LongLength }
            }
        }, Json));
        var assetIndex = await DescriptorAsync(indexPath);

        var metadata = new
        {
            id = versionId,
            type = "release",
            mainClass = "net.minecraft.client.main.Main",
            javaVersion = new { majorVersion = 21 },
            downloads = new
            {
                client = new { size = client.Size, sha1 = client.Sha1, url = "https://example.invalid/client.jar" }
            },
            assetIndex = new
            {
                id = versionId,
                size = assetIndex.Size,
                sha1 = assetIndex.Sha1,
                url = "https://example.invalid/assets.json"
            },
            libraries = new[]
            {
                new
                {
                    name = "com.example:regression:1.0",
                    downloads = new
                    {
                        artifact = new
                        {
                            path = libraryRelative,
                            size = library.Size,
                            sha1 = library.Sha1,
                            url = "https://example.invalid/regression.jar"
                        }
                    }
                }
            },
            arguments = new
            {
                game = new[] { "--username", "${auth_player_name}" },
                jvm = new[] { "-Djava.library.path=${natives_directory}", "-cp", "${classpath}" }
            }
        };
        await File.WriteAllTextAsync(Path.Combine(versionRoot, versionId + ".json"), JsonSerializer.Serialize(metadata, Json));
    }

    private static async Task CreateFabricProfileAsync(string gameRoot, string profileId, string baseVersion)
    {
        var root = Path.Combine(gameRoot, "versions", profileId);
        Directory.CreateDirectory(root);
        var metadata = new
        {
            id = profileId,
            inheritsFrom = baseVersion,
            type = "release",
            mainClass = "net.fabricmc.loader.impl.launch.knot.KnotClient",
            libraries = new[] { new { name = "net.fabricmc:fabric-loader:0.16.0" } }
        };
        await File.WriteAllTextAsync(Path.Combine(root, profileId + ".json"), JsonSerializer.Serialize(metadata, Json));
    }

    private static async Task<(long Size, string Sha1)> DescriptorAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        return (bytes.LongLength, Sha1(bytes));
    }

    private static string Sha1(byte[] bytes)
        => Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();

    private static async Task<string> SnapshotAsync(string root)
    {
        using var sha = SHA256.Create();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var nameBytes = Encoding.UTF8.GetBytes(relative + "\n");
            sha.TransformBlock(nameBytes, 0, nameBytes.Length, null, 0);
            var bytes = await File.ReadAllBytesAsync(file);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
