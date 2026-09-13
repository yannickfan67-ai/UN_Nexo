using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using UN.Nexo.Core.Launching;
using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed class MinecraftInstanceRepairService
{
    private readonly NexoPathService _paths;
    private readonly MinecraftVanillaInstallService _installer;
    private readonly MinecraftVersionManifestService _manifest;
    private readonly JavaDiscoveryService _javaDiscovery;
    private readonly MinecraftRuntimeInspector _runtimeInspector;
    private readonly JavaRuntimeProvisionService _runtimeProvisioner;

    public MinecraftInstanceRepairService(
        NexoPathService paths,
        MinecraftVanillaInstallService installer,
        MinecraftVersionManifestService manifest,
        JavaDiscoveryService javaDiscovery,
        MinecraftRuntimeInspector runtimeInspector,
        JavaRuntimeProvisionService runtimeProvisioner)
    {
        _paths = paths;
        _installer = installer;
        _manifest = manifest;
        _javaDiscovery = javaDiscovery;
        _runtimeInspector = runtimeInspector;
        _runtimeProvisioner = runtimeProvisioner;
    }

    public async Task<InstanceHealthReport> CheckAsync(
        GameInstance instance,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var issues = new List<InstanceHealthIssue>();
        var gameRoot = _paths.GetInstanceGameDirectory(instance.Id);
        var versionRoot = Path.Combine(gameRoot, "versions", instance.VersionId);
        var versionJsonPath = Path.Combine(versionRoot, $"{instance.VersionId}.json");
        var statePath = Path.Combine(_paths.GetInstanceDirectory(instance.Id), "install-state.json");

        progress?.Report("Checking install state…");
        if (!File.Exists(statePath))
        {
            issues.Add(new InstanceHealthIssue(
                "install-state-missing",
                "Install state",
                InstanceHealthLevel.Error,
                "The instance is not marked as prepared.",
                statePath,
                "Run Repair to prepare the required game files."));
        }

        if (!File.Exists(versionJsonPath))
        {
            issues.Add(new InstanceHealthIssue(
                "version-metadata-missing",
                "Version metadata",
                InstanceHealthLevel.Error,
                $"Minecraft {instance.VersionId} metadata is missing.",
                versionJsonPath,
                "Run Repair to download the official version metadata."));

            await CheckJavaAndSystemAsync(instance, issues, progress, cancellationToken);
            return BuildReport(instance, issues, null);
        }

        JsonDocument versionDocument;
        try
        {
            await using var stream = File.OpenRead(versionJsonPath);
            versionDocument = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            issues.Add(new InstanceHealthIssue(
                "version-metadata-corrupt",
                "Version metadata",
                InstanceHealthLevel.Error,
                $"Version metadata cannot be read: {ex.Message}",
                versionJsonPath,
                "Run Repair to replace the damaged metadata."));

            await CheckJavaAndSystemAsync(instance, issues, progress, cancellationToken);
            return BuildReport(instance, issues, null);
        }

        using (versionDocument)
        {
            var root = versionDocument.RootElement;
            progress?.Report("Checking Minecraft client…");
            await CheckClientAsync(instance, root, versionRoot, issues, cancellationToken);

            progress?.Report("Checking libraries and natives…");
            var nativeArchives = await CheckLibrariesAsync(root, gameRoot, issues, cancellationToken);
            await CheckExtractedNativesAsync(instance, nativeArchives, gameRoot, issues, cancellationToken);

            progress?.Report("Checking assets…");
            await CheckAssetsAsync(root, gameRoot, issues, cancellationToken);
        }

        var requiredJava = await CheckJavaAndSystemAsync(instance, issues, progress, cancellationToken);
        return BuildReport(instance, issues, requiredJava);
    }

    public async Task<InstanceHealthReport> RepairAsync(
        GameInstance instance,
        IProgress<InstallProgress>? installProgress = null,
        IProgress<string>? statusProgress = null,
        CancellationToken cancellationToken = default)
    {
        statusProgress?.Report($"Finding Minecraft {instance.VersionId} metadata…");
        var catalog = await _manifest.GetCatalogAsync(cancellationToken);
        var version = catalog.Versions.FirstOrDefault(item =>
            string.Equals(item.Id, instance.VersionId, StringComparison.Ordinal));
        if (version is null)
            throw new InvalidOperationException($"Minecraft {instance.VersionId} is not present in the official version catalog.");

        statusProgress?.Report("Repairing game files…");
        await _installer.InstallAsync(instance, version, installProgress, cancellationToken);

        statusProgress?.Report("Rebuilding legacy/virtual asset views…");
        await RebuildMappedAssetsAsync(instance, cancellationToken);

        var requiredJava = await _runtimeInspector.GetRequiredJavaMajorAsync(instance, cancellationToken) ?? 8;
        statusProgress?.Report($"Checking Java {requiredJava}…");
        var java = await _javaDiscovery.DiscoverAsync(cancellationToken);
        var matchingJava = java.FirstOrDefault(item =>
            item.Is64Bit
            && MinecraftLaunchPlanBuilder.JavaMajor(item.Version) == requiredJava);
        if (matchingJava is null)
        {
            statusProgress?.Report($"Repairing Java {requiredJava} runtime…");
            await _runtimeProvisioner.EnsureJavaAsync(requiredJava, statusProgress, cancellationToken);
        }

        statusProgress?.Report("Re-checking repaired instance…");
        return await CheckAsync(instance, statusProgress, cancellationToken);
    }

    private async Task CheckClientAsync(
        GameInstance instance,
        JsonElement root,
        string versionRoot,
        ICollection<InstanceHealthIssue> issues,
        CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("downloads", out var downloads)
            || !downloads.TryGetProperty("client", out var client))
            return;

        var jarPath = Path.Combine(versionRoot, $"{instance.VersionId}.jar");
        var sha1 = client.TryGetProperty("sha1", out var shaElement) ? shaElement.GetString() : null;
        var result = await InspectFileAsync(jarPath, sha1, cancellationToken);
        if (result == FileHealth.Healthy)
            return;

        issues.Add(new InstanceHealthIssue(
            result == FileHealth.Missing ? "client-missing" : "client-corrupt",
            "Minecraft client",
            InstanceHealthLevel.Error,
            result == FileHealth.Missing ? "The Minecraft client JAR is missing." : "The Minecraft client JAR failed SHA-1 verification.",
            jarPath,
            "Run Repair to download a verified client JAR."));
    }

    private async Task<List<NativeArchive>> CheckLibrariesAsync(
        JsonElement root,
        string gameRoot,
        ICollection<InstanceHealthIssue> issues,
        CancellationToken cancellationToken)
    {
        var nativeArchives = new List<NativeArchive>();
        if (!root.TryGetProperty("libraries", out var libraries)
            || libraries.ValueKind != JsonValueKind.Array)
            return nativeArchives;

        var librariesRoot = Path.Combine(gameRoot, "libraries");
        var missing = 0;
        var corrupt = 0;
        var samples = new List<string>();

        foreach (var library in libraries.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ShouldUseLibrary(library) || !library.TryGetProperty("downloads", out var downloads))
                continue;

            if (downloads.TryGetProperty("artifact", out var artifact))
            {
                var artifactPath = GetArtifactPath(artifact, librariesRoot);
                if (artifactPath is not null)
                {
                    var sha1 = artifact.TryGetProperty("sha1", out var artifactSha) ? artifactSha.GetString() : null;
                    var result = await InspectFileAsync(artifactPath, sha1, cancellationToken);
                    if (result == FileHealth.Missing) missing++;
                    if (result == FileHealth.Corrupt) corrupt++;
                    if (result != FileHealth.Healthy && samples.Count < 4) samples.Add(artifactPath);
                }
            }

            if (!library.TryGetProperty("natives", out var natives))
                continue;

            var osKey = GetMinecraftOsKey();
            if (!natives.TryGetProperty(osKey, out var classifierElement))
                continue;

            var classifier = (classifierElement.GetString() ?? string.Empty)
                .Replace("${arch}", Environment.Is64BitOperatingSystem ? "64" : "32", StringComparison.Ordinal);
            if (string.IsNullOrWhiteSpace(classifier)
                || !downloads.TryGetProperty("classifiers", out var classifiers)
                || !classifiers.TryGetProperty(classifier, out var nativeArtifact))
                continue;

            var excludes = new List<string> { "META-INF/" };
            if (library.TryGetProperty("extract", out var extract)
                && extract.TryGetProperty("exclude", out var excludeArray))
            {
                foreach (var item in excludeArray.EnumerateArray())
                {
                    var value = item.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                        excludes.Add(value);
                }
            }

            var archivePath = GetArtifactPath(nativeArtifact, librariesRoot);
            if (archivePath is not null)
            {
                var sha1 = nativeArtifact.TryGetProperty("sha1", out var nativeSha) ? nativeSha.GetString() : null;
                var result = await InspectFileAsync(archivePath, sha1, cancellationToken);
                if (result == FileHealth.Missing) missing++;
                if (result == FileHealth.Corrupt) corrupt++;
                if (result != FileHealth.Healthy && samples.Count < 4) samples.Add(archivePath);
                if (result == FileHealth.Healthy)
                    nativeArchives.Add(new NativeArchive(archivePath, excludes));
            }
        }

        if (missing > 0 || corrupt > 0)
        {
            issues.Add(new InstanceHealthIssue(
                "libraries-damaged",
                "Libraries",
                InstanceHealthLevel.Error,
                $"Libraries need repair: {missing} missing, {corrupt} corrupt.",
                samples.FirstOrDefault(),
                "Run Repair; verified libraries are kept and only missing/corrupt files are downloaded."));
        }

        return nativeArchives;
    }

    private static string? GetArtifactPath(JsonElement artifact, string librariesRoot)
    {
        if (!artifact.TryGetProperty("path", out var pathElement))
            return null;
        var relative = pathElement.GetString();
        return string.IsNullOrWhiteSpace(relative)
            ? null
            : Within(librariesRoot, relative);
    }

    private async Task CheckExtractedNativesAsync(
        GameInstance instance,
        IReadOnlyList<NativeArchive> nativeArchives,
        string gameRoot,
        ICollection<InstanceHealthIssue> issues,
        CancellationToken cancellationToken)
    {
        if (nativeArchives.Count == 0)
            return;

        var nativesRoot = Path.Combine(gameRoot, "natives", instance.VersionId);
        var missing = 0;
        var corrupt = 0;
        string? firstProblem = null;

        foreach (var native in nativeArchives)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var archive = ZipFile.OpenRead(native.ArchivePath);
                foreach (var entry in archive.Entries)
                {
                    var normalized = entry.FullName.Replace('\\', '/');
                    if (string.IsNullOrWhiteSpace(entry.Name)
                        || native.Excludes.Any(prefix => normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    string target;
                    try
                    {
                        target = Within(nativesRoot, normalized);
                    }
                    catch (InvalidDataException)
                    {
                        corrupt++;
                        firstProblem ??= native.ArchivePath;
                        continue;
                    }

                    if (!File.Exists(target))
                    {
                        missing++;
                        firstProblem ??= target;
                        continue;
                    }

                    await using var expectedStream = entry.Open();
                    await using var actualStream = File.OpenRead(target);
                    if (!await StreamsMatchSha1Async(expectedStream, actualStream, cancellationToken))
                    {
                        corrupt++;
                        firstProblem ??= target;
                    }
                }
            }
            catch (InvalidDataException)
            {
                corrupt++;
                firstProblem ??= native.ArchivePath;
            }
        }

        if (missing > 0 || corrupt > 0)
        {
            issues.Add(new InstanceHealthIssue(
                "natives-damaged",
                "Native libraries",
                InstanceHealthLevel.Error,
                $"Extracted natives need repair: {missing} missing, {corrupt} corrupt.",
                firstProblem,
                "Run Repair to re-extract native libraries from verified archives."));
        }
    }

    private async Task CheckAssetsAsync(
        JsonElement root,
        string gameRoot,
        ICollection<InstanceHealthIssue> issues,
        CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("assetIndex", out var assetIndex))
            return;

        var assetId = assetIndex.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(assetId))
            assetId = "legacy";
        var assetsRoot = Path.Combine(gameRoot, "assets");
        var indexPath = Path.Combine(assetsRoot, "indexes", $"{assetId}.json");
        var indexSha1 = assetIndex.TryGetProperty("sha1", out var shaElement) ? shaElement.GetString() : null;
        var indexHealth = await InspectFileAsync(indexPath, indexSha1, cancellationToken);
        if (indexHealth != FileHealth.Healthy)
        {
            issues.Add(new InstanceHealthIssue(
                indexHealth == FileHealth.Missing ? "asset-index-missing" : "asset-index-corrupt",
                "Assets",
                InstanceHealthLevel.Error,
                indexHealth == FileHealth.Missing ? "The asset index is missing." : "The asset index failed SHA-1 verification.",
                indexPath,
                "Run Repair to restore the asset index and required objects."));
            return;
        }

        JsonDocument indexDocument;
        try
        {
            await using var stream = File.OpenRead(indexPath);
            indexDocument = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            issues.Add(new InstanceHealthIssue(
                "asset-index-unreadable",
                "Assets",
                InstanceHealthLevel.Error,
                $"The asset index cannot be read: {ex.Message}",
                indexPath,
                "Run Repair to replace the asset index."));
            return;
        }

        using (indexDocument)
        {
            if (!indexDocument.RootElement.TryGetProperty("objects", out var objects))
                return;

            var mapToResources = indexDocument.RootElement.TryGetProperty("map_to_resources", out var mapElement)
                                 && mapElement.ValueKind == JsonValueKind.True;
            var checks = new List<AssetCheck>();
            foreach (var property in objects.EnumerateObject())
            {
                if (!property.Value.TryGetProperty("hash", out var hashElement))
                    continue;
                var hash = hashElement.GetString();
                if (!IsSha1(hash))
                {
                    issues.Add(new InstanceHealthIssue(
                        "asset-index-invalid-hash",
                        "Assets",
                        InstanceHealthLevel.Error,
                        $"Asset index contains an invalid SHA-1 for {property.Name}.",
                        indexPath,
                        "Run Repair to replace the asset index."));
                    return;
                }

                var objectPath = Within(Path.Combine(assetsRoot, "objects"), Path.Combine(hash![..2], hash));
                checks.Add(new AssetCheck(objectPath, hash));
                if (mapToResources)
                {
                    string resourcePath;
                    try
                    {
                        resourcePath = Within(Path.Combine(gameRoot, "resources"), property.Name);
                    }
                    catch (InvalidDataException)
                    {
                        issues.Add(new InstanceHealthIssue(
                            "asset-index-invalid-path",
                            "Assets",
                            InstanceHealthLevel.Error,
                            $"Asset index contains an unsafe resource path: {property.Name}.",
                            indexPath,
                            "Run Repair to replace the asset index."));
                        return;
                    }
                    checks.Add(new AssetCheck(resourcePath, hash));
                }
            }

            var missing = 0;
            var corrupt = 0;
            var firstProblem = new ConcurrentQueue<string>();
            await Parallel.ForEachAsync(
                checks,
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
                async (check, token) =>
                {
                    var result = await InspectFileAsync(check.Path, check.Sha1, token);
                    if (result == FileHealth.Missing) Interlocked.Increment(ref missing);
                    if (result == FileHealth.Corrupt) Interlocked.Increment(ref corrupt);
                    if (result != FileHealth.Healthy && firstProblem.Count < 4)
                        firstProblem.Enqueue(check.Path);
                });

            if (missing > 0 || corrupt > 0)
            {
                issues.Add(new InstanceHealthIssue(
                    "assets-damaged",
                    "Assets",
                    InstanceHealthLevel.Error,
                    $"Assets need repair: {missing} missing, {corrupt} corrupt.",
                    firstProblem.TryPeek(out var path) ? path : null,
                    "Run Repair to restore only missing/corrupt assets; verified objects are reused."));
            }
        }
    }

    private async Task RebuildMappedAssetsAsync(GameInstance instance, CancellationToken cancellationToken)
    {
        var gameRoot = _paths.GetInstanceGameDirectory(instance.Id);
        var versionPath = Path.Combine(gameRoot, "versions", instance.VersionId, instance.VersionId + ".json");
        if (!File.Exists(versionPath))
            return;

        using var version = await ReadJsonAsync(versionPath, cancellationToken);
        var root = version.RootElement;
        if (!root.TryGetProperty("assetIndex", out var assetIndex))
            return;

        var assetId = assetIndex.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(assetId))
            return;

        var assetsRoot = Path.Combine(gameRoot, "assets");
        var indexPath = Path.Combine(assetsRoot, "indexes", assetId + ".json");
        if (!File.Exists(indexPath))
            return;

        using var index = await ReadJsonAsync(indexPath, cancellationToken);
        var indexRoot = index.RootElement;
        if (!indexRoot.TryGetProperty("objects", out var objects))
            return;

        var legacyAssets = string.Equals(assetId, "legacy", StringComparison.OrdinalIgnoreCase)
            || (root.TryGetProperty("assets", out var assetsElement)
                && string.Equals(assetsElement.GetString(), "legacy", StringComparison.OrdinalIgnoreCase));
        var virtualAssets = legacyAssets
            || (indexRoot.TryGetProperty("virtual", out var virtualElement)
                && virtualElement.ValueKind == JsonValueKind.True);
        var mapToResources = indexRoot.TryGetProperty("map_to_resources", out var resourcesElement)
            && resourcesElement.ValueKind == JsonValueKind.True;
        if (!virtualAssets && !mapToResources)
            return;

        var objectsRoot = Path.Combine(assetsRoot, "objects");
        var virtualRoot = Path.Combine(assetsRoot, "virtual", assetId);
        var resourceRoot = Path.Combine(gameRoot, "resources");

        foreach (var property in objects.EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!property.Value.TryGetProperty("hash", out var hashElement))
                continue;
            var hash = hashElement.GetString();
            if (!IsSha1(hash))
                throw new InvalidDataException($"Asset index contains an invalid SHA-1 for {property.Name}.");

            var source = Within(objectsRoot, Path.Combine(hash![..2], hash));
            if (await InspectFileAsync(source, hash, cancellationToken) != FileHealth.Healthy)
                throw new InvalidDataException($"Verified asset object is missing or damaged: {property.Name}.");

            if (virtualAssets)
                await EnsureMappedCopyAsync(source, Within(virtualRoot, property.Name), hash, cancellationToken);
            if (mapToResources)
                await EnsureMappedCopyAsync(source, Within(resourceRoot, property.Name), hash, cancellationToken);
        }
    }

    private static async Task EnsureMappedCopyAsync(
        string source,
        string target,
        string sha1,
        CancellationToken cancellationToken)
    {
        if (await InspectFileAsync(target, sha1, cancellationToken) == FileHealth.Healthy)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: true);
    }

    private async Task<int?> CheckJavaAndSystemAsync(
        GameInstance instance,
        ICollection<InstanceHealthIssue> issues,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report("Checking Java runtime…");
        var requiredJava = await _runtimeInspector.GetRequiredJavaMajorAsync(instance, cancellationToken);
        if (requiredJava is not null)
        {
            var java = await _javaDiscovery.DiscoverAsync(cancellationToken);
            var match = java.FirstOrDefault(item =>
                item.Is64Bit && MinecraftLaunchPlanBuilder.JavaMajor(item.Version) == requiredJava.Value);
            if (match is null)
            {
                issues.Add(new InstanceHealthIssue(
                    "java-missing",
                    "Java",
                    InstanceHealthLevel.Error,
                    $"No working 64-bit Java {requiredJava.Value} runtime was detected.",
                    SuggestedAction: $"Run Repair to validate or automatically acquire Java {requiredJava.Value}."));
            }
        }

        if (OperatingSystem.IsLinux() && !LinuxLibraryExists("libXtst.so.6"))
        {
            issues.Add(new InstanceHealthIssue(
                "linux-libxtst-missing",
                "Linux dependency",
                InstanceHealthLevel.Error,
                "libXtst.so.6 is missing; legacy LWJGL 2 games such as Minecraft 1.8.9 may fail during native initialization.",
                SuggestedAction: "Install the libXtst runtime package (Debian/Mint: libxtst6) and run the check again."));
        }

        return requiredJava;
    }

    private static InstanceHealthReport BuildReport(
        GameInstance instance,
        IEnumerable<InstanceHealthIssue> issues,
        int? requiredJava)
    {
        var ordered = issues
            .OrderByDescending(item => item.Level)
            .ThenBy(item => item.Component, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Code, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new InstanceHealthReport(instance.Id, instance.VersionId, requiredJava, ordered, DateTimeOffset.UtcNow);
    }

    private static async Task<FileHealth> InspectFileAsync(
        string path,
        string? expectedSha1,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return FileHealth.Missing;
        if (string.IsNullOrWhiteSpace(expectedSha1))
            return new FileInfo(path).Length > 0 ? FileHealth.Healthy : FileHealth.Corrupt;

        try
        {
            await using var stream = File.OpenRead(path);
            using var sha1 = SHA1.Create();
            var hash = await sha1.ComputeHashAsync(stream, cancellationToken);
            var actual = Convert.ToHexString(hash).ToLowerInvariant();
            return string.Equals(actual, expectedSha1, StringComparison.OrdinalIgnoreCase)
                ? FileHealth.Healthy
                : FileHealth.Corrupt;
        }
        catch (IOException)
        {
            return FileHealth.Corrupt;
        }
    }

    private static async Task<bool> StreamsMatchSha1Async(
        Stream expected,
        Stream actual,
        CancellationToken cancellationToken)
    {
        using var expectedSha = SHA1.Create();
        using var actualSha = SHA1.Create();
        var expectedHash = await expectedSha.ComputeHashAsync(expected, cancellationToken);
        var actualHash = await actualSha.ComputeHashAsync(actual, cancellationToken);
        return expectedHash.AsSpan().SequenceEqual(actualHash);
    }

    private static async Task<JsonDocument> ReadJsonAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static bool ShouldUseLibrary(JsonElement library)
    {
        if (!library.TryGetProperty("rules", out var rules))
            return true;

        var allowed = false;
        foreach (var rule in rules.EnumerateArray())
        {
            if (!RuleMatchesCurrentMachine(rule))
                continue;
            var action = rule.TryGetProperty("action", out var actionElement)
                ? actionElement.GetString()
                : "disallow";
            allowed = string.Equals(action, "allow", StringComparison.OrdinalIgnoreCase);
        }
        return allowed;
    }

    private static bool RuleMatchesCurrentMachine(JsonElement rule)
    {
        if (rule.TryGetProperty("features", out _))
            return false;
        if (!rule.TryGetProperty("os", out var os))
            return true;

        if (os.TryGetProperty("name", out var nameElement)
            && !string.Equals(nameElement.GetString(), GetMinecraftOsKey(), StringComparison.OrdinalIgnoreCase))
            return false;

        if (os.TryGetProperty("arch", out var archElement))
        {
            var requiredArch = archElement.GetString();
            var currentArch = Environment.Is64BitOperatingSystem ? "x86_64" : "x86";
            if (!string.Equals(requiredArch, currentArch, StringComparison.OrdinalIgnoreCase)
                && !(requiredArch == "x86" && !Environment.Is64BitOperatingSystem))
                return false;
        }
        return true;
    }

    private static string GetMinecraftOsKey()
    {
        if (OperatingSystem.IsWindows()) return "windows";
        if (OperatingSystem.IsMacOS()) return "osx";
        return "linux";
    }

    private static bool LinuxLibraryExists(string fileName)
    {
        var candidates = new[]
        {
            Path.Combine("/usr/lib/x86_64-linux-gnu", fileName),
            Path.Combine("/lib/x86_64-linux-gnu", fileName),
            Path.Combine("/usr/lib64", fileName),
            Path.Combine("/lib64", fileName),
            Path.Combine("/usr/lib", fileName),
            Path.Combine("/lib", fileName)
        };
        return candidates.Any(File.Exists);
    }

    private static bool IsSha1(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Length == 40
           && value.All(Uri.IsHexDigit);

    private static string Within(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            throw new InvalidDataException("Invalid relative game path.");
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!result.StartsWith(fullRoot, comparison))
            throw new InvalidDataException("Game metadata path escapes its expected directory.");
        return result;
    }

    private sealed record NativeArchive(string ArchivePath, IReadOnlyList<string> Excludes);
    private sealed record AssetCheck(string Path, string Sha1);
    private enum FileHealth { Healthy, Missing, Corrupt }
}
