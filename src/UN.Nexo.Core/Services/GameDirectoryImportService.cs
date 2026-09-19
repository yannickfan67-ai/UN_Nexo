using System.Security.Cryptography;
using System.Text.Json;
using UN.Nexo.Core.Launching;
using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed class GameDirectoryImportService
{
    private const long MaxVersionMetadataBytes = 16L * 1024 * 1024;
    private const long FreeSpaceReserveBytes = 64L * 1024 * 1024;
    private readonly NexoPathService _paths;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "versions",
        "logs",
        "crash-reports",
        "webcache",
        "webcache2",
        ".staging",
        "launcher-logs",
        "restore-safety"
    };

    private static readonly HashSet<string> ExcludedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "launcher_accounts.json",
        "launcher_profiles.json",
        "launcher_msa_credentials.bin",
        "launcher_log.txt",
        "launcher_log1.txt",
        "launcher_log2.txt",
        ".lock"
    };

    public GameDirectoryImportService(NexoPathService paths)
    {
        _paths = paths;
    }

    public async Task<GameDirectoryImportPreview> ScanAsync(
        string sourceDirectory,
        CancellationToken cancellationToken = default)
    {
        var source = ResolveGameRoot(sourceDirectory);
        RejectDangerousSource(source);
        var warnings = new List<string>();
        var versionsRoot = Path.Combine(source, "versions");
        if (!Directory.Exists(versionsRoot))
            throw new InvalidDataException("No versions directory was found. Select .minecraft or a game directory that contains versions/.");

        var candidates = new List<(ImportVersionCandidate Candidate, DateTime LastWrite)>();
        foreach (var directory in Directory.EnumerateDirectories(versionsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                RejectReparsePoint(directory);
                var metadata = FindVersionMetadata(directory);
                if (metadata is null)
                    continue;
                RejectReparsePoint(metadata);
                EnsureDirectChild(directory, metadata, "Version metadata");
                var info = new FileInfo(metadata);
                if (info.Length <= 0 || info.Length > MaxVersionMetadataBytes)
                {
                    warnings.Add($"Skipped {Path.GetFileName(directory)}: version metadata size is invalid.");
                    continue;
                }

                await using var stream = new FileStream(
                    metadata,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out _))
                    continue;
                var id = MetadataPath.RequireSingleComponent(
                    RequireString(root, "id", "version metadata"),
                    "version id");

                string baseVersion;
                if (root.TryGetProperty("inheritsFrom", out var inherits))
                {
                    baseVersion = MetadataPath.RequireSingleComponent(
                        RequireString(root, "inheritsFrom", "version metadata"),
                        "inheritsFrom");
                }
                else
                {
                    baseVersion = id;
                }

                var (loader, detail, loaderVersion) = DetectLoader(root, baseVersion);
                var baseDirectory = ResolveVersionDirectory(
                    versionsRoot,
                    baseVersion,
                    "inheritsFrom");
                var clientJar = loader == "vanilla"
                    ? Path.Combine(directory, id + ".jar")
                    : Path.Combine(baseDirectory, baseVersion + ".jar");

                var launchableNow = loader == "vanilla";
                candidates.Add((new ImportVersionCandidate(
                    id,
                    loader,
                    baseVersion!,
                    metadata,
                    File.Exists(clientJar),
                    launchableNow,
                    detail,
                    loaderVersion),
                    info.LastWriteTimeUtc));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
            {
                warnings.Add($"Skipped {Path.GetFileName(directory)}: {ex.Message}");
            }
        }

        var ordered = candidates
            .OrderByDescending(item => item.LastWrite)
            .ThenByDescending(item => item.Candidate.VersionId, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Candidate)
            .ToArray();
        if (ordered.Length == 0)
            warnings.Add("No usable version metadata was found under versions/.");
        if (ordered.Count(item => item.Loader == "unsupported") > 0)
            warnings.Add("Some inherited/modded versions use an unsupported loader. They are shown for diagnosis but cannot be imported as runnable instances.");
        if (ordered.Any(item => item.Loader == "fabric"))
            warnings.Add("Fabric profiles can be imported now and launched after running Fabric preparation/repair in Fabric Manager.");
        if (ordered.Any(item => item.Loader == "forge"))
            warnings.Add("Forge profiles can be imported, but Forge launch support is still in progress.");

        var worldCount = CountDirectories(Path.Combine(source, "saves"));
        var modCount = CountFiles(Path.Combine(source, "mods"), "*.jar");
        var resourcePackCount = CountEntries(Path.Combine(source, "resourcepacks"));
        var shaderPackCount = CountEntries(Path.Combine(source, "shaderpacks"));
        var selectedBytes = MeasureImportableContent(source, warnings);

        return new GameDirectoryImportPreview(
            source,
            ordered,
            worldCount,
            modCount,
            resourcePackCount,
            shaderPackCount,
            selectedBytes,
            warnings);
    }

    public async Task<GameDirectoryImportResult> ImportAsync(
        GameDirectoryImportPreview preview,
        ImportVersionCandidate candidate,
        string instanceName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();

        var source = ResolveGameRoot(preview.SourceDirectory);
        RejectDangerousSource(source);
        var name = NormalizeInstanceName(instanceName);
        if (candidate.Loader == "unsupported")
            throw new NotSupportedException(
                $"{candidate.VersionId} uses an unsupported inherited/modded loader. Choose a Vanilla, Fabric or Forge entry instead.");

        var currentPreview = await ScanAsync(source, cancellationToken);
        var currentCandidate = currentPreview.Versions.FirstOrDefault(item =>
            item.VersionId.Equals(candidate.VersionId, StringComparison.Ordinal)
            && item.Loader.Equals(candidate.Loader, StringComparison.OrdinalIgnoreCase));
        if (currentCandidate is null)
            throw new InvalidOperationException("The selected version changed or disappeared since the preview. Scan the source again.");

        var existing = await new InstanceStoreService(_paths).GetAllAsync(cancellationToken);
        if (existing.Any(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"An instance named '{name}' already exists. Choose a different name.");

        _paths.EnsureDirectories();
        var instanceId = Guid.NewGuid().ToString("N");
        var targetRoot = _paths.GetInstanceDirectory(instanceId);
        if (Directory.Exists(targetRoot))
            throw new IOException("The import destination already exists.");

        var stagingParent = Path.Combine(_paths.GetDataRoot(), ".staging", "imports");
        Directory.CreateDirectory(stagingParent);
        var stagingRoot = Path.Combine(stagingParent, instanceId + ".tmp");
        var stagingGame = Path.Combine(stagingRoot, "game");
        if (Directory.Exists(stagingRoot))
            Directory.Delete(stagingRoot, recursive: true);
        Directory.CreateDirectory(stagingGame);

        var warnings = new List<string>(currentPreview.Warnings);
        var copiedBytes = 0L;
        var copiedFiles = 0;

        try
        {
            EnsureFreeSpace(stagingParent, currentPreview.SelectedContentBytes + FreeSpaceReserveBytes);
            foreach (var directory in Directory.EnumerateDirectories(source))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var namePart = Path.GetFileName(directory);
                if (ExcludedDirectories.Contains(namePart))
                    continue;
                RejectReparsePoint(directory);
                var target = ResolveChild(stagingGame, namePart);
                var copied = await CopyDirectoryAsync(directory, target, cancellationToken);
                copiedBytes = checked(copiedBytes + copied.Bytes);
                copiedFiles += copied.Files;
            }

            foreach (var file in Directory.EnumerateFiles(source))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var namePart = Path.GetFileName(file);
                if (ShouldExcludeFile(namePart))
                    continue;
                RejectReparsePoint(file);
                var copied = await CopyFileAsync(file, ResolveChild(stagingGame, namePart), cancellationToken);
                copiedBytes = checked(copiedBytes + copied);
                copiedFiles++;
            }

            var targetVersions = Path.Combine(stagingGame, "versions");
            Directory.CreateDirectory(targetVersions);
            var copiedVersionIds = new HashSet<string>(StringComparer.Ordinal) { currentCandidate.VersionId };
            if (!currentCandidate.BaseVersionId.Equals(currentCandidate.VersionId, StringComparison.Ordinal))
                copiedVersionIds.Add(currentCandidate.BaseVersionId);

            foreach (var versionId in copiedVersionIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceVersion = FindVersionDirectory(source, versionId, currentCandidate);
                if (sourceVersion is null)
                {
                    warnings.Add($"Version directory for {versionId} is missing; Nexo will need to repair/download those files later.");
                    continue;
                }
                RejectReparsePoint(sourceVersion);
                var targetVersion = ResolveChild(targetVersions, versionId);
                var copied = await CopyDirectoryAsync(sourceVersion, targetVersion, cancellationToken);
                copiedBytes = checked(copiedBytes + copied.Bytes);
                copiedFiles += copied.Files;

                if (versionId.Equals(currentCandidate.VersionId, StringComparison.Ordinal))
                {
                    var canonicalMetadata = Path.Combine(targetVersion, versionId + ".json");
                    if (!File.Exists(canonicalMetadata))
                    {
                        var copiedMetadata = Path.Combine(targetVersion, Path.GetFileName(currentCandidate.MetadataPath));
                        if (File.Exists(copiedMetadata))
                        {
                            File.Copy(copiedMetadata, canonicalMetadata);
                            copiedBytes = checked(copiedBytes + new FileInfo(canonicalMetadata).Length);
                            copiedFiles++;
                        }
                    }
                }
            }

            var instance = new GameInstance(
                instanceId,
                name,
                currentCandidate.VersionId,
                currentCandidate.Loader,
                DateTimeOffset.UtcNow,
                currentCandidate.Loader.Equals("vanilla", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : currentCandidate.BaseVersionId,
                currentCandidate.LoaderVersion);
            await WriteJsonAtomicAsync(Path.Combine(stagingRoot, "instance.json"), instance, cancellationToken);

            var prepared = currentCandidate.Loader == "vanilla"
                && await VerifyVanillaPreparedAsync(stagingGame, currentCandidate.VersionId, cancellationToken);
            if (prepared)
            {
                var state = new
                {
                    instance.Id,
                    instance.Name,
                    version = instance.VersionId,
                    loader = instance.Loader,
                    importedAt = DateTimeOffset.UtcNow,
                    source = "existing-game-directory",
                    state = "prepared"
                };
                await WriteJsonAtomicAsync(Path.Combine(stagingRoot, "install-state.json"), state, cancellationToken);
            }
            else if (currentCandidate.Loader == "vanilla")
            {
                warnings.Add("The imported Vanilla files are incomplete or failed verification. They were kept, and Nexo will reuse valid files while downloading only what is missing when the instance is prepared.");
            }
            else if (currentCandidate.Loader.Equals("fabric", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(
                    "Fabric content was preserved. Run Fabric preparation/repair in Fabric Manager before launching this imported instance.");
            }
            else
            {
                warnings.Add(
                    $"{currentCandidate.Loader} content was preserved, but this loader cannot be launched by this build yet.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            _paths.EnsureInstancesRootPhysical();
            var finalTargetRoot = _paths.GetInstanceDirectory(instanceId);
            if (Directory.Exists(finalTargetRoot) || File.Exists(finalTargetRoot))
                throw new IOException("The import destination changed before publication.");
            _paths.EnsureInstancesRootPhysical();
            Directory.Move(stagingRoot, finalTargetRoot);
            return new GameDirectoryImportResult(instance, prepared, copiedBytes, copiedFiles, warnings.Distinct().ToArray());
        }
        catch
        {
            TryDeleteDirectory(stagingRoot);
            throw;
        }
    }

    private string ResolveGameRoot(string sourceDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory))
            throw new ArgumentException("Choose a Minecraft game directory.", nameof(sourceDirectory));
        var root = Path.GetFullPath(sourceDirectory.Trim());
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Game directory does not exist: {root}");
        RejectReparsePoint(root);

        if (!Directory.Exists(Path.Combine(root, "versions"))
            && Directory.Exists(Path.Combine(root, "game", "versions")))
            root = Path.Combine(root, "game");
        return Path.GetFullPath(root);
    }

    private void RejectDangerousSource(string source)
    {
        var dataRoot = Path.GetFullPath(_paths.GetDataRoot());
        if (IsSameOrAncestor(source, dataRoot)
            || IsSameOrAncestor(dataRoot, source))
            throw new InvalidOperationException(
                "The selected source overlaps Nexo's managed data directory. Choose an external .minecraft/game folder instead.");
    }

    private static bool IsSameOrAncestor(string possibleAncestor, string child)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var ancestor = Path.GetFullPath(possibleAncestor).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (candidate.Equals(ancestor, comparison))
            return true;
        return candidate.StartsWith(ancestor + Path.DirectorySeparatorChar, comparison);
    }

    private static string? FindVersionMetadata(string versionDirectory)
    {
        var folder = Path.GetFileName(versionDirectory);
        var preferred = Path.Combine(versionDirectory, folder + ".json");
        if (File.Exists(preferred))
            return preferred;
        return Directory.EnumerateFiles(versionDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static string? FindVersionDirectory(
        string sourceRoot,
        string versionId,
        ImportVersionCandidate selected)
    {
        var versionsRoot = Path.Combine(sourceRoot, "versions");
        var canonical = ResolveVersionDirectory(
            versionsRoot,
            versionId,
            "version id");
        if (Directory.Exists(canonical))
        {
            RejectReparsePoint(canonical);
            return canonical;
        }

        if (!versionId.Equals(selected.VersionId, StringComparison.Ordinal))
            return null;

        var selectedDirectory = Path.GetDirectoryName(selected.MetadataPath);
        if (string.IsNullOrWhiteSpace(selectedDirectory)
            || !Directory.Exists(selectedDirectory))
            return null;

        RejectReparsePoint(selectedDirectory);
        EnsureDirectChild(versionsRoot, selectedDirectory, "Version directory");
        return selectedDirectory;
    }

    private static string ResolveVersionDirectory(
        string versionsRoot,
        string versionId,
        string fieldName)
    {
        var component = MetadataPath.RequireSingleComponent(versionId, fieldName);
        var fullRoot = Path.GetFullPath(versionsRoot);
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, component));
        EnsureDirectChild(fullRoot, candidate, fieldName);
        return candidate;
    }

    private static void EnsureDirectChild(
        string root,
        string child,
        string label)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullChild = Path.GetFullPath(child);
        var parent = Path.GetDirectoryName(fullChild);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (parent is null
            || !Path.TrimEndingDirectorySeparator(parent).Equals(fullRoot, comparison))
            throw new InvalidDataException(
                $"{label} must remain one direct child of the versions directory.");
    }

    private static (string Loader, string Detail, string? LoaderVersion) DetectLoader(
        JsonElement root,
        string baseVersion)
    {
        var mainClass = root.TryGetProperty("mainClass", out _)
            ? RequireString(root, "mainClass", "version metadata")
            : string.Empty;
        var libraryNames = new List<string>();
        if (root.TryGetProperty("libraries", out var libraries))
        {
            if (libraries.ValueKind != JsonValueKind.Array)
                throw InvalidMetadata("libraries", "an array");

            var index = 0;
            foreach (var library in libraries.EnumerateArray())
            {
                if (library.ValueKind != JsonValueKind.Object)
                    throw InvalidMetadata($"libraries[{index}]", "an object");

                if (library.TryGetProperty("name", out _))
                {
                    var value = RequireString(
                        library,
                        "name",
                        $"libraries[{index}]");
                    if (!string.IsNullOrWhiteSpace(value))
                        libraryNames.Add(value);
                }

                index++;
            }
        }

        const string fabricPrefix = "net.fabricmc:fabric-loader:";
        var fabricLibrary = libraryNames.FirstOrDefault(name =>
            name.StartsWith(fabricPrefix, StringComparison.OrdinalIgnoreCase));
        if (fabricLibrary is not null
            || mainClass.Contains("fabricmc", StringComparison.OrdinalIgnoreCase))
        {
            var loaderVersion = fabricLibrary is null
                ? null
                : fabricLibrary[fabricPrefix.Length..].Trim();
            if (string.IsNullOrWhiteSpace(loaderVersion))
                loaderVersion = null;
            return (
                "fabric",
                $"Fabric loader profile inheriting Minecraft {baseVersion}.",
                loaderVersion);
        }

        if (libraryNames.Any(name =>
                name.StartsWith("net.neoforged:", StringComparison.OrdinalIgnoreCase))
            || mainClass.Contains("neoforge", StringComparison.OrdinalIgnoreCase))
            return (
                "unsupported",
                $"NeoForge profile inheriting Minecraft {baseVersion}; NeoForge import/launch support is not implemented.",
                null);

        if (libraryNames.Any(name =>
                name.StartsWith("net.minecraftforge:forge:", StringComparison.OrdinalIgnoreCase))
            || mainClass.Contains("modlauncher", StringComparison.OrdinalIgnoreCase)
            || mainClass.Contains("forge", StringComparison.OrdinalIgnoreCase))
            return (
                "forge",
                $"Forge loader profile inheriting Minecraft {baseVersion}.",
                null);

        if (libraryNames.Any(name =>
                name.Contains("quiltmc", StringComparison.OrdinalIgnoreCase))
            || mainClass.Contains("quilt", StringComparison.OrdinalIgnoreCase))
            return (
                "unsupported",
                $"Quilt profile inheriting Minecraft {baseVersion}; Quilt import/launch support is not implemented.",
                null);

        if (libraryNames.Any(name =>
                name.Contains("optifine", StringComparison.OrdinalIgnoreCase)))
            return (
                "unsupported",
                $"OptiFine/inherited profile for Minecraft {baseVersion}; import it through a supported base/loader instead.",
                null);

        if (root.TryGetProperty("inheritsFrom", out _))
            return (
                "unsupported",
                $"Inherited version based on Minecraft {baseVersion}; loader could not be identified safely.",
                null);

        return ("vanilla", "Vanilla Minecraft version.", null);
    }

    private static string RequireString(
        JsonElement element,
        string propertyName,
        string context)
    {
        if (!element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String)
            throw InvalidMetadata(
                $"{context}.{propertyName}",
                "a string");

        return value.GetString() ?? string.Empty;
    }

    private static InvalidDataException InvalidMetadata(
        string propertyName,
        string expected)
        => new(
            $"Import metadata property '{propertyName}' must be {expected}.");

    private async Task<bool> VerifyVanillaPreparedAsync(
        string gameRoot,
        string versionId,
        CancellationToken cancellationToken)
    {
        try
        {
            var versionRoot = Path.Combine(gameRoot, "versions", versionId);
            var metadataPath = Path.Combine(versionRoot, versionId + ".json");
            if (!File.Exists(metadataPath))
                return false;
            await using var stream = File.OpenRead(metadataPath);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (root.TryGetProperty("inheritsFrom", out _)
                || !root.TryGetProperty("id", out var id)
                || !string.Equals(id.GetString(), versionId, StringComparison.Ordinal))
                return false;

            if (!root.TryGetProperty("downloads", out var downloads)
                || !downloads.TryGetProperty("client", out var client)
                || !await VerifyMetadataFileAsync(Path.Combine(versionRoot, versionId + ".jar"), client, cancellationToken))
                return false;

            var librariesRoot = Path.Combine(gameRoot, "libraries");
            if (root.TryGetProperty("libraries", out var libraries))
            {
                foreach (var library in libraries.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!MinecraftRules.Allows(library))
                        continue;
                    if (!library.TryGetProperty("downloads", out var libraryDownloads))
                        return false;
                    if (libraryDownloads.TryGetProperty("artifact", out var artifact))
                    {
                        var relative = artifact.TryGetProperty("path", out var pathElement) ? pathElement.GetString() : null;
                        if (string.IsNullOrWhiteSpace(relative)
                            || !await VerifyMetadataFileAsync(ResolveChild(librariesRoot, relative!), artifact, cancellationToken))
                            return false;
                    }
                    var classifier = MinecraftRules.NativeClassifier(library);
                    if (classifier is null)
                        continue;
                    if (!libraryDownloads.TryGetProperty("classifiers", out var classifiers)
                        || !classifiers.TryGetProperty(classifier, out var nativeArtifact))
                        return false;
                    var nativeRelative = nativeArtifact.TryGetProperty("path", out var nativePath) ? nativePath.GetString() : null;
                    if (string.IsNullOrWhiteSpace(nativeRelative)
                        || !await VerifyMetadataFileAsync(ResolveChild(librariesRoot, nativeRelative!), nativeArtifact, cancellationToken))
                        return false;
                }
            }

            if (!root.TryGetProperty("assetIndex", out var assetIndex))
                return false;
            var assetId = assetIndex.TryGetProperty("id", out var assetIdElement) ? assetIdElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(assetId))
                return false;
            var assetsRoot = Path.Combine(gameRoot, "assets");
            var indexPath = Path.Combine(assetsRoot, "indexes", assetId + ".json");
            if (!await VerifyMetadataFileAsync(indexPath, assetIndex, cancellationToken))
                return false;

            await using var indexStream = File.OpenRead(indexPath);
            using var indexDocument = await JsonDocument.ParseAsync(indexStream, cancellationToken: cancellationToken);
            if (indexDocument.RootElement.TryGetProperty("objects", out var objects))
            {
                foreach (var property in objects.EnumerateObject())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!property.Value.TryGetProperty("hash", out var hashElement))
                        return false;
                    var hash = hashElement.GetString();
                    if (string.IsNullOrWhiteSpace(hash) || hash.Length != 40)
                        return false;
                    var objectPath = Path.Combine(assetsRoot, "objects", hash[..2], hash);
                    var expectedSize = property.Value.TryGetProperty("size", out var size) ? size.GetInt64() : (long?)null;
                    if (!await VerifyFileAsync(objectPath, expectedSize, hash, cancellationToken))
                        return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException or CryptographicException)
        {
            return false;
        }
    }

    private static Task<bool> VerifyMetadataFileAsync(
        string path,
        JsonElement metadata,
        CancellationToken cancellationToken)
    {
        var size = metadata.TryGetProperty("size", out var sizeElement) ? sizeElement.GetInt64() : (long?)null;
        var sha1 = metadata.TryGetProperty("sha1", out var shaElement) ? shaElement.GetString() : null;
        return VerifyFileAsync(path, size, sha1, cancellationToken);
    }

    private static async Task<bool> VerifyFileAsync(
        string path,
        long? expectedSize,
        string? expectedSha1,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 0)
            return false;
        if (expectedSize.HasValue && info.Length != expectedSize.Value)
            return false;
        if (string.IsNullOrWhiteSpace(expectedSha1))
            return true;
        if (expectedSha1.Length != 40 || expectedSha1.Any(character => !Uri.IsHexDigit(character)))
            return false;

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha1 = SHA1.Create();
        var digest = await sha1.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(digest).Equals(expectedSha1, StringComparison.OrdinalIgnoreCase);
    }

    private long MeasureImportableContent(string source, ICollection<string> warnings)
    {
        long total = 0;
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(directory);
            if (ExcludedDirectories.Contains(name))
                continue;
            try
            {
                total = checked(total + MeasureDirectorySafe(directory));
            }
            catch (InvalidDataException ex)
            {
                warnings.Add($"Cannot import {name}: {ex.Message}");
            }
        }
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var name = Path.GetFileName(file);
            if (ShouldExcludeFile(name))
                continue;
            try
            {
                RejectReparsePoint(file);
                total = checked(total + new FileInfo(file).Length);
            }
            catch (InvalidDataException ex)
            {
                warnings.Add($"Cannot import {name}: {ex.Message}");
            }
        }

        // versions are added separately at import time; reserve a modest margin in the estimate.
        return checked(total + 256L * 1024 * 1024);
    }

    private static long MeasureDirectorySafe(string root)
    {
        RejectReparsePoint(root);
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(root))
        {
            RejectReparsePoint(file);
            total = checked(total + new FileInfo(file).Length);
        }
        foreach (var directory in Directory.EnumerateDirectories(root))
            total = checked(total + MeasureDirectorySafe(directory));
        return total;
    }

    private static async Task<(long Bytes, int Files)> CopyDirectoryAsync(
        string source,
        string target,
        CancellationToken cancellationToken)
    {
        RejectReparsePoint(source);
        Directory.CreateDirectory(target);
        long bytes = 0;
        var files = 0;
        foreach (var file in Directory.EnumerateFiles(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectReparsePoint(file);
            var size = await CopyFileAsync(file, ResolveChild(target, Path.GetFileName(file)), cancellationToken);
            bytes = checked(bytes + size);
            files++;
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectReparsePoint(directory);
            var copied = await CopyDirectoryAsync(directory, ResolveChild(target, Path.GetFileName(directory)), cancellationToken);
            bytes = checked(bytes + copied.Bytes);
            files += copied.Files;
        }
        return (bytes, files);
    }

    private static async Task<long> CopyFileAsync(
        string source,
        string target,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(
            target,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, cancellationToken);
        return new FileInfo(source).Length;
    }

    private async Task WriteJsonAtomicAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var temp = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(value, _json), cancellationToken);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDeleteFile(temp);
            throw;
        }
    }

    private static bool ShouldExcludeFile(string name)
    {
        if (ExcludedFiles.Contains(name))
            return true;
        return name.StartsWith("launcher_", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".lck", StringComparison.OrdinalIgnoreCase);
    }

    private static int CountDirectories(string path)
        => Directory.Exists(path) ? Directory.EnumerateDirectories(path).Count() : 0;

    private static int CountFiles(string path, string pattern)
        => Directory.Exists(path) ? Directory.EnumerateFiles(path, pattern, SearchOption.TopDirectoryOnly).Count() : 0;

    private static int CountEntries(string path)
        => Directory.Exists(path)
            ? Directory.EnumerateFileSystemEntries(path, "*", SearchOption.TopDirectoryOnly).Count()
            : 0;

    private static string NormalizeInstanceName(string value)
    {
        var name = (value ?? string.Empty).Trim();
        if (name.Length is < 1 or > 80 || name.Any(char.IsControl))
            throw new ArgumentException("Instance name must be 1-80 printable characters.", nameof(value));
        return name;
    }

    private static void EnsureFreeSpace(string path, long requiredBytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(root))
                return;
            var drive = new DriveInfo(root);
            if (drive.IsReady && drive.AvailableFreeSpace < requiredBytes)
                throw new IOException(
                    $"Not enough free space. Need about {requiredBytes / (1024 * 1024)} MiB, " +
                    $"available {drive.AvailableFreeSpace / (1024 * 1024)} MiB.");
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException)
        {
            // Atomic staging still protects the destination when the platform cannot report free space.
        }
    }

    private static string ResolveChild(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            throw new InvalidDataException("Invalid relative import path.");
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var child = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!child.StartsWith(rootFull, comparison))
            throw new InvalidDataException("An imported path escaped the staging directory.");
        return child;
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Symbolic links/reparse points are not imported: {path}");
    }

    private static void TryDeleteDirectory(string path)
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

    private static void TryDeleteFile(string path)
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
}
