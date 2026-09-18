using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
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

        var gameRoot = _paths.GetInstanceGameDirectory(instance.Id);
        var versionRoot = Path.Combine(gameRoot, "versions", version.Id);
        var librariesRoot = Path.Combine(gameRoot, "libraries");
        var assetsRoot = Path.Combine(gameRoot, "assets");
        var nativesRoot = Path.Combine(gameRoot, "natives", version.Id);

        Directory.CreateDirectory(versionRoot);
        Directory.CreateDirectory(librariesRoot);
        Directory.CreateDirectory(Path.Combine(assetsRoot, "indexes"));
        Directory.CreateDirectory(Path.Combine(assetsRoot, "objects"));
        Directory.CreateDirectory(nativesRoot);

        Report(progress, new InstallProgress("Version metadata", 0, 1, version.Id));
        var versionJsonPath = Path.Combine(versionRoot, $"{version.Id}.json");
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
            await DownloadFileAsync(clientUrl, Path.Combine(versionRoot, $"{version.Id}.jar"), clientSha1, "Minecraft client", 0, 1, progress, cancellationToken);
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
        var localPath = Path.Combine(librariesRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
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

    private static void ExtractNativeArchive(string archivePath, string targetDirectory, IReadOnlyList<string> excludes)
    {
        Directory.CreateDirectory(targetDirectory);
        using var archive = ZipFile.OpenRead(archivePath);
        var targetRoot = Path.GetFullPath(targetDirectory) + Path.DirectorySeparatorChar;

        foreach (var entry in archive.Entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(entry.Name)
                || excludes.Any(prefix => normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                continue;

            var destination = Path.GetFullPath(Path.Combine(targetDirectory, normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(targetRoot, StringComparison.Ordinal))
                continue;

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
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
                using var response = await _httpClient.GetAsync(candidate, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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
