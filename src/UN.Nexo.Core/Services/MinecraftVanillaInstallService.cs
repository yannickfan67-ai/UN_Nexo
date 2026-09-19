using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UN.Nexo.Core.Launching;
using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed class MinecraftVanillaInstallService
{
    private const long MaxVersionMetadataBytes = 8L * 1024 * 1024;
    private const long MaxAssetIndexBytes = 64L * 1024 * 1024;
    private const long MaxChecksumBytes = 1024;
    private readonly HttpClient _httpClient;
    private readonly NexoPathService _paths;
    private readonly DownloadSourceService _downloadSources;
    private readonly TimeSpan _transferIdleTimeout;
    private readonly object _activityGate = new();
    private CancellationTokenSource? _activeInstallCancellation;

    public event Action<InstallProgress>? ProgressChanged;
    public event Action<bool>? InstallActivityChanged;

    public MinecraftVanillaInstallService(
        HttpClient httpClient,
        NexoPathService paths,
        DownloadSourceService downloadSources,
        TimeSpan? transferIdleTimeout = null)
    {
        _httpClient = httpClient;
        _paths = paths;
        _downloadSources = downloadSources;
        _transferIdleTimeout = transferIdleTimeout ?? TimeSpan.FromSeconds(30);
        if (_transferIdleTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(transferIdleTimeout), "Transfer idle timeout must be positive.");
    }

    public bool IsInstalling
    {
        get
        {
            lock (_activityGate)
                return _activeInstallCancellation is not null;
        }
    }

    public bool CancelCurrentInstall()
    {
        lock (_activityGate)
        {
            if (_activeInstallCancellation is null)
                return false;
            _activeInstallCancellation.Cancel();
            return true;
        }
    }

    public async Task InstallAsync(
        GameInstance instance,
        MinecraftVersionInfo version,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        CancellationTokenSource activityCancellation;
        lock (_activityGate)
        {
            if (_activeInstallCancellation is not null)
                throw new InvalidOperationException("Another Minecraft file preparation task is already running.");

            activityCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeInstallCancellation = activityCancellation;
        }

        PublishActivity(true);
        try
        {
            await InstallCoreAsync(instance, version, progress, activityCancellation.Token);
        }
        finally
        {
            lock (_activityGate)
            {
                if (ReferenceEquals(_activeInstallCancellation, activityCancellation))
                    _activeInstallCancellation = null;
            }
            activityCancellation.Dispose();
            PublishActivity(false);
        }
    }

    private async Task InstallCoreAsync(
        GameInstance instance,
        MinecraftVersionInfo version,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(version.Url))
            throw new InvalidOperationException("The selected Minecraft version has no metadata URL.");

        var versionId = MetadataPath.RequireSingleComponent(
            version.Id,
            "version.id");
        var gameRoot = _paths.GetInstanceGameDirectory(instance.Id);
        var versionsRoot = Path.Combine(gameRoot, "versions");
        var nativesParent = Path.Combine(gameRoot, "natives");
        var versionRoot = MetadataPath.ResolveSingleComponent(
            versionsRoot,
            versionId,
            string.Empty,
            "version.id");
        var librariesRoot = Path.Combine(gameRoot, "libraries");
        var assetsRoot = Path.Combine(gameRoot, "assets");
        var nativesRoot = MetadataPath.ResolveSingleComponent(
            nativesParent,
            versionId,
            string.Empty,
            "version.id");

        Directory.CreateDirectory(versionRoot);
        Directory.CreateDirectory(librariesRoot);
        Directory.CreateDirectory(Path.Combine(assetsRoot, "indexes"));
        Directory.CreateDirectory(Path.Combine(assetsRoot, "objects"));
        Directory.CreateDirectory(Path.Combine(assetsRoot, "log_configs"));
        Directory.CreateDirectory(nativesRoot);

        Report(progress, new InstallProgress("Version metadata", 0, 1, versionId));
        var versionJsonPath = MetadataPath.ResolveSingleComponent(
            versionRoot,
            versionId,
            ".json",
            "version.id");
        await DownloadFileAsync(
            version.Url,
            versionJsonPath,
            NormalizeSha1(
                version.Sha1,
                "version manifest sha1"),
            expectedSize: null,
            requireIntegrity: false,
            "Version metadata",
            0,
            1,
            progress,
            cancellationToken,
            MaxVersionMetadataBytes);
        Report(progress, new InstallProgress("Version metadata", 1, 1, versionId, Detail: "Version metadata ready"));

        using var versionDocument = await ReadBoundedJsonFileAsync(
            versionJsonPath,
            MaxVersionMetadataBytes,
            "Version metadata",
            cancellationToken);
        var root = versionDocument.RootElement;

        JsonElement client;
        JsonElement assetIndex;
        string assetId;
        try
        {
            ValidatePreparationMetadata(
                root,
                versionId,
                out client,
                out assetIndex,
                out assetId);
        }
        catch (InvalidDataException)
        {
            TryDeleteFile(versionJsonPath);
            throw;
        }

        Report(progress, new InstallProgress("Minecraft client", 0, 1, versionId));
        var clientUrl = RequireString(
            client,
            "url",
            "downloads.client.url");
        var clientSha1 = OptionalSha1(
            client,
            "sha1",
            "downloads.client.sha1");
        var clientSize = OptionalSize(
            client,
            "size",
            "downloads.client.size");
        RequireBinaryIntegrity(
            clientSha1,
            clientSize,
            "downloads.client");
        var clientPath = MetadataPath.ResolveSingleComponent(
            versionRoot,
            versionId,
            ".jar",
            "version.id");
        await DownloadFileAsync(
            clientUrl,
            clientPath,
            clientSha1,
            clientSize,
            requireIntegrity: true,
            "Minecraft client",
            0,
            1,
            progress,
            cancellationToken);
        Report(progress, new InstallProgress(
            "Minecraft client",
            1,
            1,
            versionId,
            Detail: "Client JAR ready"));

        var libraryJobs = CollectLibraryDownloads(root, librariesRoot, nativesRoot);
        var libraryCompleted = 0;
        Report(progress, new InstallProgress("Libraries", 0, libraryJobs.Count));
        foreach (var job in libraryJobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sha1 = job.Sha1;
            if (sha1 is null && job.ChecksumUrl is not null)
                sha1 = await ResolveSha1SidecarAsync(
                    job.ChecksumUrl,
                    cancellationToken);

            RequireBinaryIntegrity(
                sha1,
                job.Size,
                $"library '{Path.GetFileName(job.Path)}'");
            await DownloadFileAsync(
                job.Url,
                job.Path,
                sha1,
                job.Size,
                requireIntegrity: true,
                "Libraries",
                libraryCompleted,
                libraryJobs.Count,
                progress,
                cancellationToken);
            if (job.ExtractTo is not null)
                ExtractNativeArchive(job.Path, job.ExtractTo, job.Excludes);

            libraryCompleted++;
            Report(progress, new InstallProgress(
                "Libraries",
                libraryCompleted,
                libraryJobs.Count,
                Path.GetFileName(job.Path)));
        }

        await DownloadLoggingConfigurationAsync(
            root,
            assetsRoot,
            progress,
            cancellationToken);

        var assetUrl = RequireString(
            assetIndex,
            "url",
            "assetIndex.url");
        var assetSha1 = OptionalSha1(
            assetIndex,
            "sha1",
            "assetIndex.sha1");
        var assetSize = OptionalSize(
            assetIndex,
            "size",
            "assetIndex.size");
        var indexPath = MetadataPath.ResolveSingleComponent(
            Path.Combine(assetsRoot, "indexes"),
            assetId,
            ".json",
            "assetIndex.id");

        Report(progress, new InstallProgress("Asset index", 0, 1, assetId));
        await DownloadFileAsync(
            assetUrl,
            indexPath,
            assetSha1,
            assetSize,
            requireIntegrity: false,
            "Asset index",
            0,
            1,
            progress,
            cancellationToken,
            MaxAssetIndexBytes);
        Report(progress, new InstallProgress(
            "Asset index",
            1,
            1,
            assetId,
            Detail: "Asset index ready"));

        await DownloadAssetsAsync(
            indexPath,
            assetsRoot,
            progress,
            cancellationToken);

        var state = new
        {
            instance.Id,
            instance.Name,
            version = versionId,
            loader = instance.Loader,
            installedAt = DateTimeOffset.UtcNow,
            source = _downloadSources.SourceId,
            state = "prepared"
        };
        var statePath = Path.Combine(_paths.GetInstanceDirectory(instance.Id), "install-state.json");
        await AtomicJsonFile.WriteAsync(
            statePath,
            state,
            new JsonSerializerOptions { WriteIndented = true },
            cancellationToken);

        Report(progress, new InstallProgress("Ready", 1, 1, versionId, Detail: "All required Vanilla files are ready"));
    }

    private static void ValidatePreparationMetadata(
        JsonElement root,
        string expectedVersionId,
        out JsonElement client,
        out JsonElement assetIndex,
        out string assetId)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw InvalidMetadata("root", "an object");

        var declaredId = RequireString(root, "id", "id");
        if (!string.Equals(
                declaredId,
                expectedVersionId,
                StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Version metadata id '{declaredId}' does not match selected version '{expectedVersionId}'.");

        var downloads = RequireObject(
            root,
            "downloads",
            "downloads");
        client = RequireObject(
            downloads,
            "client",
            "downloads.client");
        _ = RequireString(
            client,
            "url",
            "downloads.client.url");
        _ = OptionalSha1(
            client,
            "sha1",
            "downloads.client.sha1");
        _ = OptionalSize(
            client,
            "size",
            "downloads.client.size");

        assetIndex = RequireObject(
            root,
            "assetIndex",
            "assetIndex");
        assetId = MetadataPath.RequireSingleComponent(
            RequireString(
                assetIndex,
                "id",
                "assetIndex.id"),
            "assetIndex.id");
        _ = RequireString(
            assetIndex,
            "url",
            "assetIndex.url");
        _ = OptionalSha1(
            assetIndex,
            "sha1",
            "assetIndex.sha1");
        _ = OptionalSize(
            assetIndex,
            "size",
            "assetIndex.size");
    }

    private static JsonElement RequireObject(
        JsonElement element,
        string propertyName,
        string displayName)
    {
        if (!element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Object)
            throw InvalidMetadata(displayName, "an object");

        return value;
    }

    private static string RequireString(
        JsonElement element,
        string propertyName,
        string displayName)
    {
        if (!element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
            throw InvalidMetadata(displayName, "a non-empty string");

        return value.GetString()!;
    }

    private static string? OptionalString(
        JsonElement element,
        string propertyName,
        string displayName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw InvalidMetadata(displayName, "a string");

        return value.GetString();
    }

    private static string? OptionalSha1(
        JsonElement element,
        string propertyName,
        string displayName)
        => NormalizeSha1(
            OptionalString(
                element,
                propertyName,
                displayName),
            displayName);

    private static string? NormalizeSha1(
        string? value,
        string displayName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (!IsSha1(value))
            throw InvalidMetadata(
                displayName,
                "a 40-character hexadecimal SHA-1");
        return value.ToLowerInvariant();
    }

    private static long? OptionalSize(
        JsonElement element,
        string propertyName,
        string displayName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var size)
            || size <= 0)
            throw InvalidMetadata(
                displayName,
                "a positive integer");
        return size;
    }

    private static void RequireBinaryIntegrity(
        string? sha1,
        long? size,
        string displayName)
    {
        if (sha1 is null && size is null)
            throw new InvalidDataException(
                $"{displayName} must provide SHA-1 or a positive size before it can be treated as a required binary artifact.");
    }

    private static InvalidDataException InvalidMetadata(
        string propertyName,
        string expected)
        => new(
            $"Vanilla version metadata property '{propertyName}' must be {expected}.");

    private async Task DownloadLoggingConfigurationAsync(
        JsonElement root,
        string assetsRoot,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("logging", out var logging))
            return;
        if (logging.ValueKind != JsonValueKind.Object)
            throw InvalidMetadata("logging", "an object");
        if (!logging.TryGetProperty("client", out var clientLogging))
            return;
        if (clientLogging.ValueKind != JsonValueKind.Object)
            throw InvalidMetadata("logging.client", "an object");
        if (!clientLogging.TryGetProperty("file", out var file))
            return;
        if (file.ValueKind != JsonValueKind.Object)
            throw InvalidMetadata(
                "logging.client.file",
                "an object");

        var id = MetadataPath.RequireSingleComponent(
            RequireString(
                file,
                "id",
                "logging.client.file.id"),
            "logging.client.file.id");
        var url = RequireString(
            file,
            "url",
            "logging.client.file.url");
        var sha1 = OptionalSha1(
            file,
            "sha1",
            "logging.client.file.sha1");
        var size = OptionalSize(
            file,
            "size",
            "logging.client.file.size");
        RequireBinaryIntegrity(
            sha1,
            size,
            "logging.client.file");

        var target = MetadataPath.ResolveSingleComponent(
            Path.Combine(assetsRoot, "log_configs"),
            id,
            string.Empty,
            "logging.client.file.id");

        Report(
            progress,
            new InstallProgress(
                "Logging configuration",
                0,
                1,
                id));
        await DownloadFileAsync(
            url,
            target,
            sha1,
            size,
            requireIntegrity: true,
            "Logging configuration",
            0,
            1,
            progress,
            cancellationToken);
        Report(
            progress,
            new InstallProgress(
                "Logging configuration",
                1,
                1,
                id,
                Detail: "Logging configuration ready"));
    }

    private async Task DownloadAssetsAsync(
        string indexPath,
        string assetsRoot,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var document = await ReadBoundedJsonFileAsync(
            indexPath,
            MaxAssetIndexBytes,
            "Asset index",
            cancellationToken);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("objects", out var objects)
            || objects.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Asset index must contain an 'objects' object.");

        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in objects.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException(
                    $"Asset index entry '{property.Name}' must be an object.");
            if (!property.Value.TryGetProperty("hash", out var hashElement)
                || hashElement.ValueKind != JsonValueKind.String)
                throw new InvalidDataException(
                    $"Asset index entry '{property.Name}' must contain a string 'hash'.");

            var hash = hashElement.GetString();
            if (!IsSha1(hash))
                throw new InvalidDataException(
                    $"Asset index entry '{property.Name}' contains an invalid SHA-1 hash.");

            hashes.Add(hash!.ToLowerInvariant());
        }

        var objectsRoot = Path.Combine(assetsRoot, "objects");
        var assetHashes = hashes.ToArray();
        var completed = 0;
        Report(progress, new InstallProgress("Assets", 0, assetHashes.Length));

        await Parallel.ForEachAsync(
            assetHashes,
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken },
            async (hash, token) =>
            {
                var prefix = hash[..2];
                var target = ResolveAssetObjectPath(objectsRoot, hash);
                var url = $"https://resources.download.minecraft.net/{prefix}/{hash}";
                var before = Volatile.Read(ref completed);
                await DownloadFileAsync(
                    url,
                    target,
                    hash,
                    expectedSize: null,
                    requireIntegrity: true,
                    "Assets",
                    before,
                    assetHashes.Length,
                    progress,
                    token);
                var value = Interlocked.Increment(ref completed);
                Report(progress, new InstallProgress("Assets", value, assetHashes.Length, hash));
            });
    }

    private static string ResolveAssetObjectPath(string objectsRoot, string hash)
    {
        if (!IsSha1(hash))
            throw new InvalidDataException("Asset object hash must be a 40-character hexadecimal SHA-1.");

        var canonicalHash = hash.ToLowerInvariant();
        var root = Path.GetFullPath(objectsRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var target = Path.GetFullPath(
            Path.Combine(root, canonicalHash[..2], canonicalHash));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var rootPrefix = root + Path.DirectorySeparatorChar;

        if (!target.StartsWith(rootPrefix, comparison))
            throw new InvalidDataException("Asset object path escapes assets/objects.");

        return target;
    }

    private static bool IsSha1(string? value)
        => value is { Length: 40 } && value.All(Uri.IsHexDigit);

    private List<DownloadJob> CollectLibraryDownloads(
        JsonElement root,
        string librariesRoot,
        string nativesRoot)
    {
        var jobs = new List<DownloadJob>();
        if (!root.TryGetProperty("libraries", out var libraries))
            return jobs;
        if (libraries.ValueKind != JsonValueKind.Array)
            throw InvalidMetadata("libraries", "an array");

        var index = 0;
        foreach (var library in libraries.EnumerateArray())
        {
            if (library.ValueKind != JsonValueKind.Object)
                throw InvalidMetadata(
                    $"libraries[{index}]",
                    "an object");

            if (!MinecraftRules.Allows(library))
            {
                index++;
                continue;
            }

            var artifactAdded = false;
            JsonElement downloads = default;
            var hasDownloads = library.TryGetProperty(
                "downloads",
                out downloads);
            if (hasDownloads)
            {
                if (downloads.ValueKind != JsonValueKind.Object)
                    throw InvalidMetadata(
                        $"libraries[{index}].downloads",
                        "an object");

                if (downloads.TryGetProperty(
                        "artifact",
                        out var artifact))
                {
                    AddDownloadJob(
                        jobs,
                        artifact,
                        librariesRoot,
                        null,
                        [],
                        $"libraries[{index}].downloads.artifact");
                    artifactAdded = true;
                }
            }

            if (!artifactAdded
                && library.TryGetProperty(
                    "name",
                    out var nameElement))
            {
                if (nameElement.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(
                        nameElement.GetString()))
                    throw InvalidMetadata(
                        $"libraries[{index}].name",
                        "a non-empty Maven coordinate");

                AddMavenFallbackJob(
                    jobs,
                    library,
                    nameElement.GetString()!,
                    librariesRoot,
                    index);
            }

            var classifier = MinecraftRules.NativeClassifier(
                library);
            if (!string.IsNullOrWhiteSpace(classifier))
            {
                if (!hasDownloads
                    || !downloads.TryGetProperty(
                        "classifiers",
                        out var classifiers)
                    || classifiers.ValueKind
                        != JsonValueKind.Object
                    || !classifiers.TryGetProperty(
                        classifier,
                        out var nativeArtifact))
                    throw new InvalidDataException(
                        $"Library {index} requires native classifier '{classifier}' but does not declare a matching download.");

                var excludes = new List<string>
                {
                    "META-INF/"
                };
                if (library.TryGetProperty(
                        "extract",
                        out var extract))
                {
                    if (extract.ValueKind
                        != JsonValueKind.Object)
                        throw InvalidMetadata(
                            $"libraries[{index}].extract",
                            "an object");
                    if (extract.TryGetProperty(
                            "exclude",
                            out var excludeArray))
                    {
                        if (excludeArray.ValueKind
                            != JsonValueKind.Array)
                            throw InvalidMetadata(
                                $"libraries[{index}].extract.exclude",
                                "an array");
                        foreach (var item
                                 in excludeArray.EnumerateArray())
                        {
                            if (item.ValueKind
                                != JsonValueKind.String)
                                throw InvalidMetadata(
                                    $"libraries[{index}].extract.exclude[]",
                                    "a string");
                            var value = item.GetString();
                            if (!string.IsNullOrWhiteSpace(value))
                                excludes.Add(value);
                        }
                    }
                }

                AddDownloadJob(
                    jobs,
                    nativeArtifact,
                    librariesRoot,
                    nativesRoot,
                    excludes,
                    $"libraries[{index}].downloads.classifiers.{classifier}");
            }

            index++;
        }

        return jobs;
    }

    private static void AddMavenFallbackJob(
        ICollection<DownloadJob> jobs,
        JsonElement library,
        string coordinate,
        string librariesRoot,
        int index)
    {
        var relativePath =
            MavenArtifactPath.FromCoordinate(coordinate);
        var localPath = MetadataPath.ResolveRelativePath(
            librariesRoot,
            relativePath,
            "Maven library path");

        var repositoryText = library.TryGetProperty(
            "url",
            out var repositoryElement)
            ? repositoryElement.ValueKind
                == JsonValueKind.String
                ? repositoryElement.GetString()
                : throw InvalidMetadata(
                    $"libraries[{index}].url",
                    "a string")
            : null;
        var repository = TrustedDownloadPolicy.RequireTrustedUri(
            string.IsNullOrWhiteSpace(repositoryText)
                ? "https://libraries.minecraft.net/"
                : repositoryText!,
            $"libraries[{index}].url");

        var baseText = repository.AbsoluteUri.EndsWith(
            "/",
            StringComparison.Ordinal)
            ? repository.AbsoluteUri
            : repository.AbsoluteUri + "/";
        var artifactUri = new Uri(
            new Uri(baseText, UriKind.Absolute),
            relativePath.Replace(
                Path.DirectorySeparatorChar,
                '/'));
        artifactUri = TrustedDownloadPolicy.RequireTrustedUri(
            artifactUri.AbsoluteUri,
            $"libraries[{index}] Maven artifact URL");

        jobs.Add(new DownloadJob(
            artifactUri.AbsoluteUri,
            localPath,
            Sha1: null,
            Size: null,
            ChecksumUrl: artifactUri.AbsoluteUri + ".sha1",
            ExtractTo: null,
            Excludes: []));
    }

    private static void AddDownloadJob(
        ICollection<DownloadJob> jobs,
        JsonElement element,
        string librariesRoot,
        string? extractTo,
        IReadOnlyList<string> excludes,
        string displayName)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw InvalidMetadata(
                displayName,
                "an object");

        var url = RequireString(
            element,
            "url",
            displayName + ".url");
        var relativePath = RequireString(
            element,
            "path",
            displayName + ".path");
        var sha1 = OptionalSha1(
            element,
            "sha1",
            displayName + ".sha1");
        var size = OptionalSize(
            element,
            "size",
            displayName + ".size");
        var localPath = MetadataPath.ResolveRelativePath(
            librariesRoot,
            relativePath,
            "library artifact path");

        string? checksumUrl = null;
        if (sha1 is null && size is null)
        {
            var trusted = TrustedDownloadPolicy.RequireTrustedUri(
                url,
                displayName + ".url");
            if (trusted.Host.Equals(
                    "libraries.minecraft.net",
                    StringComparison.OrdinalIgnoreCase)
                || trusted.Host.Equals(
                    "maven.fabricmc.net",
                    StringComparison.OrdinalIgnoreCase))
                checksumUrl = trusted.AbsoluteUri + ".sha1";
        }

        jobs.Add(new DownloadJob(
            url,
            localPath,
            sha1,
            size,
            checksumUrl,
            extractTo,
            excludes));
    }

    internal static void ExtractNativeArchive(
        string archivePath,
        string targetDirectory,
        IReadOnlyList<string> excludes)
    {
        var targetRoot = Path.GetFullPath(targetDirectory);
        ValidateExistingPathChain(targetRoot);

        var parent = Path.GetDirectoryName(targetRoot)
            ?? throw new InvalidDataException("Native extraction target has no parent directory.");
        Directory.CreateDirectory(parent);
        ValidateExistingPathChain(parent);

        var staging = Path.Combine(
            parent,
            ".native-extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);

        try
        {
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                foreach (var entry in archive.Entries)
                {
                    var relative = ValidateArchiveRelativePath(entry.FullName);
                    if (IsZipSymlink(entry))
                        throw new InvalidDataException(
                            $"Native archive contains a symbolic-link entry: {entry.FullName}");

                    if (excludes.Any(prefix =>
                            relative.StartsWith(
                                prefix.Replace('\\', '/'),
                                StringComparison.OrdinalIgnoreCase)))
                        continue;

                    var stagedPath = ResolveContained(staging, relative);
                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal)
                        || string.IsNullOrWhiteSpace(entry.Name))
                    {
                        Directory.CreateDirectory(stagedPath);
                        continue;
                    }

                    Directory.CreateDirectory(
                        Path.GetDirectoryName(stagedPath)
                        ?? throw new InvalidDataException("Native archive entry has no parent directory."));
                    using var input = entry.Open();
                    using var output = new FileStream(
                        stagedPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None);
                    input.CopyTo(output);
                }
            }

            ValidateNativePublishPlan(staging, targetRoot);
            PublishNativeTree(staging, targetRoot);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    private static string ValidateArchiveRelativePath(string value)
    {
        var normalized = value.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized)
            || normalized.StartsWith("/", StringComparison.Ordinal)
            || Path.IsPathRooted(normalized)
            || normalized.Any(char.IsControl))
            throw new InvalidDataException($"Unsafe native archive entry: {value}");

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(part => part is "." or ".."))
            throw new InvalidDataException($"Unsafe native archive entry: {value}");

        return string.Join('/', parts);
    }

    private static string ResolveContained(string root, string relative)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var result = Path.GetFullPath(
            Path.Combine(
                fullRoot,
                relative.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!result.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison))
            throw new InvalidDataException("Native archive entry escaped the extraction root.");
        return result;
    }

    private static void ValidateNativePublishPlan(string staging, string targetRoot)
    {
        ValidateExistingPathChain(targetRoot);

        foreach (var directory in Directory.EnumerateDirectories(
                     staging,
                     "*",
                     SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(staging, directory);
            var destination = ResolveContained(
                targetRoot,
                relative.Replace(Path.DirectorySeparatorChar, '/'));
            ValidateTargetComponents(targetRoot, destination, finalMustBeFile: false);
        }

        foreach (var file in Directory.EnumerateFiles(
                     staging,
                     "*",
                     SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(staging, file);
            var destination = ResolveContained(
                targetRoot,
                relative.Replace(Path.DirectorySeparatorChar, '/'));
            ValidateTargetComponents(targetRoot, destination, finalMustBeFile: true);
        }
    }

    private static void ValidateTargetComponents(
        string targetRoot,
        string destination,
        bool finalMustBeFile)
    {
        var fullRoot = Path.GetFullPath(targetRoot);
        var relative = Path.GetRelativePath(fullRoot, destination);
        var parts = relative.Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);
        var current = fullRoot;

        if (Directory.Exists(current) || File.Exists(current))
            RejectReparsePoint(current);

        for (var index = 0; index < parts.Length; index++)
        {
            current = Path.Combine(current, parts[index]);
            var isFinal = index == parts.Length - 1;
            if (!Directory.Exists(current) && !File.Exists(current))
                continue;

            RejectReparsePoint(current);
            if (!isFinal && File.Exists(current))
                throw new InvalidDataException(
                    $"Native extraction path collides with a file: {current}");
            if (isFinal && finalMustBeFile && Directory.Exists(current))
                throw new InvalidDataException(
                    $"Native extraction file collides with a directory: {current}");
        }
    }

    private static void PublishNativeTree(string staging, string targetRoot)
    {
        Directory.CreateDirectory(targetRoot);
        RejectReparsePoint(targetRoot);

        foreach (var directory in Directory.EnumerateDirectories(
                     staging,
                     "*",
                     SearchOption.AllDirectories)
                 .OrderBy(path => path.Count(character =>
                     character == Path.DirectorySeparatorChar)))
        {
            var relative = Path.GetRelativePath(staging, directory);
            var destination = ResolveContained(
                targetRoot,
                relative.Replace(Path.DirectorySeparatorChar, '/'));
            ValidateTargetComponents(targetRoot, destination, finalMustBeFile: false);
            Directory.CreateDirectory(destination);
            RejectReparsePoint(destination);
        }

        foreach (var file in Directory.EnumerateFiles(
                     staging,
                     "*",
                     SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(staging, file);
            var destination = ResolveContained(
                targetRoot,
                relative.Replace(Path.DirectorySeparatorChar, '/'));
            ValidateTargetComponents(targetRoot, destination, finalMustBeFile: true);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(file, destination, overwrite: true);
        }
    }

    private static void ValidateExistingPathChain(string path)
    {
        var full = Path.GetFullPath(path);
        var current = new DirectoryInfo(full);
        while (current is not null)
        {
            if (current.Exists)
                RejectReparsePoint(current.FullName);
            current = current.Parent;
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException(
                $"Native extraction path contains a symbolic link/reparse point: {path}");
    }

    private static bool IsZipSymlink(ZipArchiveEntry entry)
    {
        var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
        return unixMode == 0xA000;
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
            // Best-effort quarantine cleanup only.
        }
    }

    private async Task DownloadFileAsync(
        string url,
        string path,
        string? expectedSha1,
        long? expectedSize,
        bool requireIntegrity,
        string stage,
        int completed,
        int total,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken,
        long? maxBytes = null)
    {
        expectedSha1 = NormalizeSha1(
            expectedSha1,
            stage + " SHA-1");
        if (expectedSize is <= 0)
            throw new InvalidDataException(
                $"{stage} expected size must be positive.");
        if (requireIntegrity)
            RequireBinaryIntegrity(
                expectedSha1,
                expectedSize,
                stage);

        Directory.CreateDirectory(
            Path.GetDirectoryName(path)!);
        var item = Path.GetFileName(path);

        if (File.Exists(path)
            && (maxBytes is null
                || new FileInfo(path).Length <= maxBytes.Value))
        {
            var cached = await VerifyFileAsync(
                path,
                expectedSha1,
                expectedSize,
                cancellationToken);
            if (cached is not null)
            {
                Report(
                    progress,
                    new InstallProgress(
                        stage,
                        completed,
                        total,
                        item,
                        "Cache",
                        Detail: cached));
                return;
            }
        }

        var temporaryPath =
            path + "." + Guid.NewGuid().ToString("N") + ".part";
        Exception? lastException = null;
        var candidates = _downloadSources.GetCandidates(url);

        for (var candidateIndex = 0;
             candidateIndex < candidates.Count;
             candidateIndex++)
        {
            var candidate = candidates[candidateIndex];
            var source = SourceLabel(candidate);
            var fallback = candidateIndex > 0;

            TryDeleteFile(temporaryPath);

            Report(
                progress,
                new InstallProgress(
                    stage,
                    completed,
                    total,
                    item,
                    source,
                    IsFallback: fallback,
                    Detail: fallback
                        ? "Retrying with fallback source"
                        : "Connecting…"));

            try
            {
                using var response =
                    await TrustedHttpDownload.SendGetAsync(
                        _httpClient,
                        candidate,
                        stage,
                        cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    lastException = new HttpRequestException(
                        $"HTTP {(int)response.StatusCode} from {new Uri(candidate).Host}.");
                    Report(
                        progress,
                        new InstallProgress(
                            stage,
                            completed,
                            total,
                            item,
                            source,
                            IsFallback: fallback,
                            Detail: lastException.Message));
                    continue;
                }

                var contentLength =
                    response.Content.Headers.ContentLength;
                if (contentLength is < 0)
                    throw new InvalidDataException(
                        $"{stage} response declared an invalid content length.");
                if (maxBytes is not null
                    && contentLength is > 0
                    && contentLength.Value > maxBytes.Value)
                    throw new InvalidDataException(
                        $"{stage} response exceeds the {maxBytes.Value}-byte limit.");
                if (expectedSize.HasValue
                    && contentLength.HasValue
                    && contentLength.Value != expectedSize.Value)
                    throw new InvalidDataException(
                        $"{stage} response length {contentLength.Value} does not match expected size {expectedSize.Value}.");

                long downloaded;
                await using (var input =
                             await response.Content.ReadAsStreamAsync(
                                 cancellationToken))
                await using (var output = new FileStream(
                                 temporaryPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 128 * 1024,
                                 FileOptions.Asynchronous
                                 | FileOptions.SequentialScan))
                {
                    downloaded = await CopyWithIdleTimeoutAsync(
                        input,
                        output,
                        candidate,
                        (bytes, bytesPerSecond) =>
                            Report(
                                progress,
                                new InstallProgress(
                                    stage,
                                    completed,
                                    total,
                                    item,
                                    source,
                                    bytes,
                                    contentLength,
                                    bytesPerSecond,
                                    fallback,
                                    fallback
                                        ? "Downloading from fallback source"
                                        : "Downloading")),
                        cancellationToken,
                        maxBytes);
                }

                if (downloaded <= 0)
                    throw new InvalidDataException(
                        $"{stage} response body was empty.");
                if (contentLength.HasValue
                    && downloaded != contentLength.Value)
                    throw new InvalidDataException(
                        $"{stage} response ended after {downloaded} bytes; expected {contentLength.Value}.");
                if (expectedSize.HasValue
                    && downloaded != expectedSize.Value)
                    throw new InvalidDataException(
                        $"{stage} downloaded {downloaded} bytes; expected {expectedSize.Value}.");

                var verification = await VerifyFileAsync(
                    temporaryPath,
                    expectedSha1,
                    expectedSize,
                    cancellationToken);
                if (expectedSha1 is not null
                    && verification is null)
                    throw new InvalidDataException(
                        $"SHA-1 verification failed from {new Uri(candidate).Host}.");
                if (expectedSize.HasValue
                    && verification is null)
                    throw new InvalidDataException(
                        $"Size verification failed from {new Uri(candidate).Host}.");

                File.Move(
                    temporaryPath,
                    path,
                    overwrite: true);
                var detail = expectedSha1 is not null
                    ? "SHA-1 verified"
                    : expectedSize.HasValue
                        ? "Size-validated (no digest supplied)"
                        : "Downloaded; metadata validation pending";
                Report(
                    progress,
                    new InstallProgress(
                        stage,
                        completed,
                        total,
                        item,
                        source,
                        downloaded,
                        contentLength,
                        0,
                        fallback,
                        detail));
                return;
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                TryDeleteFile(temporaryPath);
                throw;
            }
            catch (Exception ex) when (
                ex is HttpRequestException
                or IOException
                or TaskCanceledException
                or TimeoutException
                or InvalidDataException)
            {
                TryDeleteFile(temporaryPath);
                lastException = ex;
                Report(
                    progress,
                    new InstallProgress(
                        stage,
                        completed,
                        total,
                        item,
                        source,
                        IsFallback: fallback,
                        Detail: candidateIndex + 1
                                < candidates.Count
                            ? $"{ex.Message} Switching source…"
                            : ex.Message));
            }
        }

        TryDeleteFile(temporaryPath);
        throw lastException
              ?? new HttpRequestException(
                  $"No download source was available for {Path.GetFileName(path)}.");
    }

    private async Task<string> ResolveSha1SidecarAsync(
        string url,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        var candidates = _downloadSources.GetCandidates(url);
        foreach (var candidate in candidates)
        {
            try
            {
                using var response =
                    await TrustedHttpDownload.SendGetAsync(
                        _httpClient,
                        candidate,
                        "Maven checksum",
                        cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    lastException = new HttpRequestException(
                        $"HTTP {(int)response.StatusCode} from {new Uri(candidate).Host}.");
                    continue;
                }

                if (response.Content.Headers.ContentLength
                    is > MaxChecksumBytes)
                    throw new InvalidDataException(
                        "Maven SHA-1 sidecar exceeds the 1024-byte limit.");

                await using var input =
                    await response.Content.ReadAsStreamAsync(
                        cancellationToken);
                await using var output = new MemoryStream();
                _ = await CopyWithIdleTimeoutAsync(
                    input,
                    output,
                    candidate,
                    static (_, _) => { },
                    cancellationToken,
                    MaxChecksumBytes);

                var text = Encoding.ASCII
                    .GetString(output.ToArray())
                    .Trim();
                var token = text.Split(
                        [' ', '\t', '\r', '\n'],
                        StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault();
                if (!IsSha1(token))
                    throw new InvalidDataException(
                        "Maven SHA-1 sidecar did not contain a valid 40-character digest.");

                return token!.ToLowerInvariant();
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (
                ex is HttpRequestException
                or IOException
                or TaskCanceledException
                or TimeoutException
                or InvalidDataException)
            {
                lastException = ex;
            }
        }

        throw lastException
              ?? new InvalidDataException(
                  "No trusted Maven SHA-1 sidecar was available.");
    }

    private async Task<long> CopyWithIdleTimeoutAsync(
        Stream input,
        Stream output,
        string candidate,
        Action<long, double> progress,
        CancellationToken cancellationToken,
        long? maxBytes = null)
    {
        var buffer = new byte[128 * 1024];
        var downloaded = 0L;
        var stopwatch = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;

        while (true)
        {
            using var idle =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            idle.CancelAfter(_transferIdleTimeout);

            int read;
            try
            {
                read = await input.ReadAsync(
                    buffer.AsMemory(0, buffer.Length),
                    idle.Token);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Download from {new Uri(candidate).Host} made no progress for {_transferIdleTimeout.TotalSeconds:0.#} seconds.");
            }

            if (read == 0)
            {
                progress(
                    downloaded,
                    downloaded
                    / Math.Max(
                        stopwatch.Elapsed.TotalSeconds,
                        0.001));
                return downloaded;
            }

            if (maxBytes is not null
                && downloaded + read > maxBytes.Value)
                throw new InvalidDataException(
                    $"Download from {new Uri(candidate).Host} exceeded the {maxBytes.Value}-byte limit.");

            await output.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);
            downloaded += read;

            if (stopwatch.Elapsed - lastReport
                >= TimeSpan.FromMilliseconds(200))
            {
                progress(
                    downloaded,
                    downloaded
                    / Math.Max(
                        stopwatch.Elapsed.TotalSeconds,
                        0.001));
                lastReport = stopwatch.Elapsed;
            }
        }
    }

    private static async Task<JsonDocument> ReadBoundedJsonFileAsync(
        string path,
        long maxBytes,
        string label,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException(
                $"{label} file is missing.",
                path);
        if (info.Length > maxBytes)
            throw new InvalidDataException(
                $"{label} file exceeds the {maxBytes}-byte limit.");

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous
                | FileOptions.SequentialScan);
            return await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"{label} contains malformed JSON.",
                ex);
        }
    }

    private static async Task<string?> VerifyFileAsync(
        string path,
        string? expectedSha1,
        long? expectedSize,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 0)
            return null;

        if (expectedSize.HasValue
            && info.Length != expectedSize.Value)
            return null;

        if (expectedSha1 is null)
            return expectedSize.HasValue
                ? "Size-validated existing file (no digest supplied)"
                : null;

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous
            | FileOptions.SequentialScan);
        using var sha1 = SHA1.Create();
        var hash = await sha1.ComputeHashAsync(
            stream,
            cancellationToken);
        var actual = Convert.ToHexString(hash)
            .ToLowerInvariant();
        return string.Equals(
                actual,
                expectedSha1,
                StringComparison.OrdinalIgnoreCase)
            ? "SHA-1 verified existing file"
            : null;
    }

    private static string SourceLabel(string candidate)
    {
        var host = new Uri(candidate).Host;
        if (host.Contains("bmclapi", StringComparison.OrdinalIgnoreCase))
            return "BMCLAPI";
        if (host.EndsWith("minecraft.net", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("mojang.com", StringComparison.OrdinalIgnoreCase))
            return "Official";
        return host;
    }

    private void Report(IProgress<InstallProgress>? progress, InstallProgress value)
    {
        progress?.Report(value);
        try
        {
            ProgressChanged?.Invoke(value);
        }
        catch
        {
        }
    }

    private void PublishActivity(bool active)
    {
        try
        {
            InstallActivityChanged?.Invoke(active);
        }
        catch
        {
        }
    }

    private sealed record DownloadJob(
        string Url,
        string Path,
        string? Sha1,
        long? Size,
        string? ChecksumUrl,
        string? ExtractTo,
        IReadOnlyList<string> Excludes);
}
