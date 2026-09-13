using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed class MinecraftVanillaInstallService
{
    private readonly HttpClient _httpClient;
    private readonly NexoPathService _paths;
    private readonly DownloadSourceService _downloadSources;

    public MinecraftVanillaInstallService(HttpClient httpClient, NexoPathService paths, DownloadSourceService downloadSources)
    {
        _httpClient = httpClient;
        _paths = paths;
        _downloadSources = downloadSources;
    }

    public async Task InstallAsync(
        GameInstance instance,
        MinecraftVersionInfo version,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
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

        progress?.Report(new InstallProgress("Version metadata", 0, 1, version.Id));
        var versionJsonPath = Path.Combine(versionRoot, $"{version.Id}.json");
        await DownloadFileAsync(version.Url, versionJsonPath, version.Sha1, cancellationToken);
        progress?.Report(new InstallProgress("Version metadata", 1, 1, version.Id));

        await using var versionStream = File.OpenRead(versionJsonPath);
        using var versionDocument = await JsonDocument.ParseAsync(versionStream, cancellationToken: cancellationToken);
        var root = versionDocument.RootElement;

        if (root.TryGetProperty("downloads", out var downloads)
            && downloads.TryGetProperty("client", out var client))
        {
            progress?.Report(new InstallProgress("Minecraft client", 0, 1, version.Id));
            var clientUrl = client.GetProperty("url").GetString() ?? throw new InvalidDataException("Client URL missing.");
            var clientSha1 = client.TryGetProperty("sha1", out var clientSha) ? clientSha.GetString() : null;
            await DownloadFileAsync(clientUrl, Path.Combine(versionRoot, $"{version.Id}.jar"), clientSha1, cancellationToken);
            progress?.Report(new InstallProgress("Minecraft client", 1, 1, version.Id));
        }

        var libraryJobs = CollectLibraryDownloads(root, librariesRoot, nativesRoot);
        var libraryCompleted = 0;
        progress?.Report(new InstallProgress("Libraries", 0, libraryJobs.Count));
        foreach (var job in libraryJobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DownloadFileAsync(job.Url, job.Path, job.Sha1, cancellationToken);
            if (job.ExtractTo is not null)
                ExtractNativeArchive(job.Path, job.ExtractTo, job.Excludes);

            libraryCompleted++;
            progress?.Report(new InstallProgress("Libraries", libraryCompleted, libraryJobs.Count, Path.GetFileName(job.Path)));
        }

        if (root.TryGetProperty("assetIndex", out var assetIndex))
        {
            var assetId = assetIndex.GetProperty("id").GetString() ?? "legacy";
            var assetUrl = assetIndex.GetProperty("url").GetString() ?? throw new InvalidDataException("Asset index URL missing.");
            var assetSha1 = assetIndex.TryGetProperty("sha1", out var indexSha) ? indexSha.GetString() : null;
            var indexPath = Path.Combine(assetsRoot, "indexes", $"{assetId}.json");

            progress?.Report(new InstallProgress("Asset index", 0, 1, assetId));
            await DownloadFileAsync(assetUrl, indexPath, assetSha1, cancellationToken);
            progress?.Report(new InstallProgress("Asset index", 1, 1, assetId));

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

        progress?.Report(new InstallProgress("Ready", 1, 1, version.Id));
    }

    private async Task DownloadAssetsAsync(
        string indexPath,
        string assetsRoot,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(indexPath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("objects", out var objects))
            return;

        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in objects.EnumerateObject())
        {
            if (property.Value.TryGetProperty("hash", out var hashElement))
            {
                var hash = hashElement.GetString();
                if (!string.IsNullOrWhiteSpace(hash))
                    hashes.Add(hash);
            }
        }

        var assetHashes = hashes.ToArray();
        var completed = 0;
        progress?.Report(new InstallProgress("Assets", 0, assetHashes.Length));

        await Parallel.ForEachAsync(
            assetHashes,
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken },
            async (hash, token) =>
            {
                var prefix = hash[..2];
                var target = Path.Combine(assetsRoot, "objects", prefix, hash);
                var url = $"https://resources.download.minecraft.net/{prefix}/{hash}";
                await DownloadFileAsync(url, target, hash, token);
                var value = Interlocked.Increment(ref completed);
                progress?.Report(new InstallProgress("Assets", value, assetHashes.Length, hash));
            });
    }

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

    private async Task DownloadFileAsync(string url, string path, string? expectedSha1, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (File.Exists(path) && await HashMatchesAsync(path, expectedSha1, cancellationToken))
            return;

        var temporaryPath = path + ".part";
        Exception? lastException = null;

        foreach (var candidate in _downloadSources.GetCandidates(url))
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);

            try
            {
                using var response = await _httpClient.GetAsync(candidate, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    lastException = new HttpRequestException($"HTTP {(int)response.StatusCode} from {new Uri(candidate).Host}.");
                    continue;
                }

                await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
                {
                    await input.CopyToAsync(output, cancellationToken);
                }

                if (!await HashMatchesAsync(temporaryPath, expectedSha1, cancellationToken))
                {
                    File.Delete(temporaryPath);
                    lastException = new InvalidDataException($"SHA-1 verification failed from {new Uri(candidate).Host}.");
                    continue;
                }

                File.Move(temporaryPath, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                lastException = ex;
            }
        }

        if (File.Exists(temporaryPath))
            File.Delete(temporaryPath);

        throw lastException ?? new HttpRequestException($"No download source was available for {Path.GetFileName(path)}.");
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

    private sealed record DownloadJob(
        string Url,
        string Path,
        string? Sha1,
        string? ExtractTo,
        IReadOnlyList<string> Excludes);
}
