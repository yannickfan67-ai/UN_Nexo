using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using UN.Nexo.Core.Launching;
using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed class MinecraftVanillaInstallService
{
    private const long MaxVersionMetadataBytes = 8L * 1024 * 1024;
    private const long MaxAssetIndexBytes = 64L * 1024 * 1024;
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
            "version id");
        var gameRoot = _paths.GetInstanceGameDirectory(instance.Id);
        var versionRoot = MetadataPath.ResolveSingleComponent(
            Path.Combine(gameRoot, "versions"),
            versionId,
            string.Empty,
            "version id");
        var librariesRoot = Path.Combine(gameRoot, "libraries");
        var assetsRoot = Path.Combine(gameRoot, "assets");
        var nativesRoot = MetadataPath.ResolveSingleComponent(
            Path.Combine(gameRoot, "natives"),
            versionId,
            string.Empty,
            "version id");

        Directory.CreateDirectory(versionRoot);
        Directory.CreateDirectory(librariesRoot);
        Directory.CreateDirectory(Path.Combine(assetsRoot, "indexes"));
        Directory.CreateDirectory(Path.Combine(assetsRoot, "objects"));
        Directory.CreateDirectory(nativesRoot);

        Report(progress, new InstallProgress("Version metadata", 0, 1, version.Id));
        var versionJsonPath = Path.Combine(versionRoot, $"{versionId}.json");
        await DownloadFileAsync(
            version.Url,
            versionJsonPath,
            version.Sha1,
            "Version metadata",
            0,
            1,
            progress,
            cancellationToken,
            MaxVersionMetadataBytes);
        Report(progress, new InstallProgress("Version metadata", 1, 1, version.Id, Detail: "Version metadata ready"));

        using var versionDocument = await ReadBoundedJsonFileAsync(
            versionJsonPath,
            MaxVersionMetadataBytes,
            "Version metadata",
            cancellationToken);
        var root = versionDocument.RootElement;

        if (root.TryGetProperty("downloads", out var downloads)
            && downloads.TryGetProperty("client", out var client))
        {
            Report(progress, new InstallProgress("Minecraft client", 0, 1, version.Id));
            var clientUrl = client.GetProperty("url").GetString() ?? throw new InvalidDataException("Client URL missing.");
            var clientSha1 = client.TryGetProperty("sha1", out var clientSha) ? clientSha.GetString() : null;
            await DownloadFileAsync(clientUrl, Path.Combine(versionRoot, $"{versionId}.jar"), clientSha1, "Minecraft client", 0, 1, progress, cancellationToken);
            Report(progress, new InstallProgress("Minecraft client", 1, 1, version.Id, Detail: "Client JAR ready"));
        }

        var libraryJobs = CollectLibraryDownloads(root, librariesRoot, nativesRoot);
        var libraryCompleted = 0;
        Report(progress, new InstallProgress("Libraries", 0, libraryJobs.Count));
        foreach (var job in libraryJobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DownloadFileAsync(job.Url, job.Path, job.Sha1, "Libraries", libraryCompleted, libraryJobs.Count, progress, cancellationToken);
            if (job.ExtractTo is not null)
                ExtractNativeArchive(job.Path, job.ExtractTo, job.Excludes);

            libraryCompleted++;
            Report(progress, new InstallProgress("Libraries", libraryCompleted, libraryJobs.Count, Path.GetFileName(job.Path)));
        }

        if (root.TryGetProperty("assetIndex", out var assetIndex))
        {
            var assetId = MetadataPath.RequireSingleComponent(
                assetIndex.GetProperty("id").GetString(),
                "assetIndex.id");
            var assetUrl = assetIndex.GetProperty("url").GetString() ?? throw new InvalidDataException("Asset index URL missing.");
            var assetSha1 = assetIndex.TryGetProperty("sha1", out var indexSha) ? indexSha.GetString() : null;
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
                "Asset index",
                0,
                1,
                progress,
                cancellationToken,
                MaxAssetIndexBytes);
            Report(progress, new InstallProgress("Asset index", 1, 1, assetId, Detail: "Asset index ready"));

            await DownloadAssetsAsync(indexPath, assetsRoot, progress, cancellationToken);
        }

        var state = new
        {
            instance.Id,
            instance.Name,
            version = version.Id,
            loader = instance.Loader,
            installedAt = DateTimeOffset.UtcNow,
            source = _downloadSources.SourceId,
            state = "prepared"
        };
        var statePath = Path.Combine(_paths.GetInstanceDirectory(instance.Id), "install-state.json");
        await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);

        Report(progress, new InstallProgress("Ready", 1, 1, version.Id, Detail: "All required Vanilla files are ready"));
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
                await DownloadFileAsync(url, target, hash, "Assets", before, assetHashes.Length, progress, token);
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

    private List<DownloadJob> CollectLibraryDownloads(JsonElement root, string librariesRoot, string nativesRoot)
    {
        var jobs = new List<DownloadJob>();
        if (!root.TryGetProperty("libraries", out var libraries))
            return jobs;

        foreach (var library in libraries.EnumerateArray())
        {
            if (!ShouldUseLibrary(library) || !library.TryGetProperty("downloads", out var downloads))
                continue;

            if (downloads.TryGetProperty("artifact", out var artifact))
                AddDownloadJob(jobs, artifact, librariesRoot, null, []);

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

            AddDownloadJob(jobs, nativeArtifact, librariesRoot, nativesRoot, excludes);
        }

        return jobs;
    }

    private static void AddDownloadJob(
        ICollection<DownloadJob> jobs,
        JsonElement element,
        string librariesRoot,
        string? extractTo,
        IReadOnlyList<string> excludes)
    {
        if (!element.TryGetProperty("url", out var urlElement)
            || !element.TryGetProperty("path", out var pathElement))
            return;

        var url = urlElement.GetString();
        var relativePath = pathElement.GetString();
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(relativePath))
            return;

        var sha1 = element.TryGetProperty("sha1", out var shaElement) ? shaElement.GetString() : null;
        var localPath = MinecraftLaunchPlanBuilder.Within(
            librariesRoot,
            relativePath);
        jobs.Add(new DownloadJob(url, localPath, sha1, extractTo, excludes));
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

        if (os.TryGetProperty("name", out var nameElement))
        {
            var required = nameElement.GetString();
            if (!string.Equals(required, GetMinecraftOsKey(), StringComparison.OrdinalIgnoreCase))
                return false;
        }

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

    private async Task DownloadFileAsync(
        string url,
        string path,
        string? expectedSha1,
        string stage,
        int completed,
        int total,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken,
        long? maxBytes = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var item = Path.GetFileName(path);

        if (File.Exists(path)
            && (maxBytes is null || new FileInfo(path).Length <= maxBytes.Value)
            && await HashMatchesAsync(path, expectedSha1, cancellationToken))
        {
            Report(progress, new InstallProgress(stage, completed, total, item, "Cache", Detail: "Verified existing file"));
            return;
        }

        var temporaryPath = path + ".part";
        Exception? lastException = null;
        var candidates = _downloadSources.GetCandidates(url);

        for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            var candidate = candidates[candidateIndex];
            var source = SourceLabel(candidate);
            var fallback = candidateIndex > 0;

            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);

            Report(progress, new InstallProgress(
                stage,
                completed,
                total,
                item,
                source,
                IsFallback: fallback,
                Detail: fallback ? "Retrying with fallback source" : "Connecting…"));

            try
            {
                using var response = await TrustedHttpDownload.SendGetAsync(
                    _httpClient,
                    candidate,
                    stage,
                    cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    lastException = new HttpRequestException($"HTTP {(int)response.StatusCode} from {new Uri(candidate).Host}.");
                    Report(progress, new InstallProgress(stage, completed, total, item, source, IsFallback: fallback, Detail: lastException.Message));
                    continue;
                }

                var contentLength = response.Content.Headers.ContentLength;
                if (maxBytes is not null && contentLength is > 0 && contentLength.Value > maxBytes.Value)
                    throw new InvalidDataException(
                        $"{stage} response exceeds the {maxBytes.Value}-byte limit.");

                await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
                {
                    await CopyWithIdleTimeoutAsync(
                        input,
                        output,
                        candidate,
                        (bytes, bytesPerSecond) => Report(progress, new InstallProgress(
                            stage,
                            completed,
                            total,
                            item,
                            source,
                            bytes,
                            contentLength,
                            bytesPerSecond,
                            fallback,
                            fallback ? "Downloading from fallback source" : "Downloading")),
                        cancellationToken,
                        maxBytes);
                }

                if (!await HashMatchesAsync(temporaryPath, expectedSha1, cancellationToken))
                {
                    File.Delete(temporaryPath);
                    lastException = new InvalidDataException($"SHA-1 verification failed from {new Uri(candidate).Host}.");
                    Report(progress, new InstallProgress(stage, completed, total, item, source, IsFallback: fallback, Detail: lastException.Message));
                    continue;
                }

                File.Move(temporaryPath, path, overwrite: true);
                Report(progress, new InstallProgress(
                    stage,
                    completed,
                    total,
                    item,
                    source,
                    contentLength ?? new FileInfo(path).Length,
                    contentLength,
                    0,
                    fallback,
                    "Verified"));
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or TimeoutException or InvalidDataException)
            {
                lastException = ex;
                Report(progress, new InstallProgress(
                    stage,
                    completed,
                    total,
                    item,
                    source,
                    IsFallback: fallback,
                    Detail: candidateIndex + 1 < candidates.Count
                        ? $"{ex.Message} Switching source…"
                        : ex.Message));
            }
        }

        if (File.Exists(temporaryPath))
            File.Delete(temporaryPath);

        throw lastException ?? new HttpRequestException($"No download source was available for {Path.GetFileName(path)}.");
    }

    private async Task CopyWithIdleTimeoutAsync(
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
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            idle.CancelAfter(_transferIdleTimeout);

            int read;
            try
            {
                read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), idle.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Download from {new Uri(candidate).Host} made no progress for {_transferIdleTimeout.TotalSeconds:0.#} seconds.");
            }

            if (read == 0)
            {
                progress(downloaded, downloaded / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001));
                return;
            }

            if (maxBytes is not null && downloaded + read > maxBytes.Value)
                throw new InvalidDataException(
                    $"Download from {new Uri(candidate).Host} exceeded the {maxBytes.Value}-byte limit.");

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloaded += read;

            if (stopwatch.Elapsed - lastReport >= TimeSpan.FromMilliseconds(200))
            {
                progress(downloaded, downloaded / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001));
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
            throw new FileNotFoundException($"{label} file is missing.", path);
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
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{label} contains malformed JSON.", ex);
        }
    }

    private static async Task<bool> HashMatchesAsync(string path, string? expectedSha1, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expectedSha1))
            return File.Exists(path);

        await using var stream = File.OpenRead(path);
        using var sha1 = SHA1.Create();
        var hash = await sha1.ComputeHashAsync(stream, cancellationToken);
        var actual = Convert.ToHexString(hash).ToLowerInvariant();
        return string.Equals(actual, expectedSha1, StringComparison.OrdinalIgnoreCase);
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
        string? ExtractTo,
        IReadOnlyList<string> Excludes);
}
