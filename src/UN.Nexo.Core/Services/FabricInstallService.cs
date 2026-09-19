using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UN.Nexo.Core.Launching;
using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed class FabricInstallService(
    HttpClient httpClient,
    NexoPathService paths,
    MinecraftVanillaInstallService vanillaInstaller,
    FabricMetaService fabricMeta,
    TimeSpan? transferIdleTimeout = null)
{
    private const int MaxChecksumBytes = 1024;
    private readonly TimeSpan _transferIdleTimeout = transferIdleTimeout is null
        ? TimeSpan.FromSeconds(30)
        : transferIdleTimeout.Value > TimeSpan.Zero
            ? transferIdleTimeout.Value
            : throw new ArgumentOutOfRangeException(
                nameof(transferIdleTimeout),
                "Fabric transfer idle timeout must be positive.");
    public async Task PrepareAsync(
        GameInstance instance,
        MinecraftVersionInfo baseVersion,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!instance.Loader.Equals("fabric", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Fabric preparation requires a Fabric instance.");

        var instanceRoot = paths.GetInstanceDirectory(instance.Id);
        var gameRoot = paths.GetInstanceGameDirectory(instance.Id);
        var versionsRoot = Path.Combine(gameRoot, "versions");
        var profileRoot = Path.Combine(versionsRoot, instance.VersionId);
        var profilePath = Path.Combine(profileRoot, instance.VersionId + ".json");
        Directory.CreateDirectory(profileRoot);

        JsonDocument profile;
        var persistDownloadedProfile = false;
        if (File.Exists(profilePath))
        {
            await using var profileStream = File.OpenRead(profilePath);
            profile = await JsonDocument.ParseAsync(profileStream, cancellationToken: cancellationToken);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(instance.LoaderVersion))
                throw new InvalidOperationException(
                    "Fabric Loader version is missing and no imported Fabric profile is available.");
            profile = await fabricMeta.GetProfileAsync(
                baseVersion.Id,
                instance.LoaderVersion,
                cancellationToken);
            persistDownloadedProfile = true;
        }

        using (profile)
        {
            var root = profile.RootElement;
            ValidateProfile(root, instance.VersionId, baseVersion.Id);
            if (persistDownloadedProfile)
                await WriteProfileAtomicAsync(profilePath, root, cancellationToken);

            var loaderVersion = instance.LoaderVersion ?? ReadLoaderVersion(root)
                ?? throw new InvalidDataException("Fabric profile does not declare a Fabric Loader library.");

            var statePath = Path.Combine(instanceRoot, "install-state.json");
            byte[]? previousState = null;
            if (File.Exists(statePath))
                previousState = await File.ReadAllBytesAsync(statePath, cancellationToken);

            try
            {
                Report(progress, new InstallProgress("Vanilla base", 0, 1, baseVersion.Id));
                var vanillaProxy = instance with
                {
                    VersionId = baseVersion.Id,
                    Loader = "vanilla",
                    BaseVersionId = null,
                    LoaderVersion = null
                };
                await vanillaInstaller.InstallAsync(vanillaProxy, baseVersion, progress, cancellationToken);
                if (File.Exists(statePath))
                    File.Delete(statePath);
                Report(progress, new InstallProgress("Vanilla base", 1, 1, baseVersion.Id));

                var libraries = CollectLibraries(root, Path.Combine(gameRoot, "libraries"));
                var completed = 0;
                Report(progress, new InstallProgress("Fabric libraries", 0, libraries.Count));
                foreach (var library in libraries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await DownloadLibraryAsync(library, cancellationToken);
                    completed++;
                    Report(progress, new InstallProgress(
                        "Fabric libraries",
                        completed,
                        libraries.Count,
                        Path.GetFileName(library.Path)));
                }

                var state = new
                {
                    instance.Id,
                    instance.Name,
                    version = instance.VersionId,
                    baseVersion = baseVersion.Id,
                    loader = "fabric",
                    loaderVersion,
                    launchVersion = instance.VersionId,
                    installedAt = DateTimeOffset.UtcNow,
                    source = "fabric-meta",
                    state = "prepared"
                };
                await AtomicJsonFile.WriteAsync(
                    statePath,
                    state,
                    new JsonSerializerOptions { WriteIndented = true },
                    cancellationToken);
                Report(progress, new InstallProgress(
                    "Ready",
                    1,
                    1,
                    instance.VersionId,
                    Detail: $"Fabric Loader {loaderVersion} is ready"));
            }
            catch
            {
                TryDeleteFile(statePath);
                if (previousState is not null)
                    await File.WriteAllBytesAsync(statePath, previousState, CancellationToken.None);
                throw;
            }
        }
    }

    public async Task<string?> GetBaseVersionIdAsync(
        GameInstance instance,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(instance.BaseVersionId))
            return instance.BaseVersionId;

        var profilePath = Path.Combine(
            paths.GetInstanceGameDirectory(instance.Id),
            "versions",
            instance.VersionId,
            instance.VersionId + ".json");
        if (!File.Exists(profilePath))
            return null;

        await using var stream = File.OpenRead(profilePath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Fabric profile root must be an object.");
        if (!root.TryGetProperty("inheritsFrom", out var inherits))
            return null;
        if (inherits.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Fabric profile property 'inheritsFrom' must be a string.");
        return inherits.GetString();
    }

    public static string? ReadLoaderVersion(JsonElement profile)
    {
        if (profile.ValueKind != JsonValueKind.Object
            || !profile.TryGetProperty("libraries", out var libraries)
            || libraries.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var library in libraries.EnumerateArray())
        {
            if (library.ValueKind != JsonValueKind.Object
                || !library.TryGetProperty("name", out var nameElement)
                || nameElement.ValueKind != JsonValueKind.String)
                continue;
            var name = nameElement.GetString();
            const string prefix = "net.fabricmc:fabric-loader:";
            if (name is not null && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return name[prefix.Length..];
        }
        return null;
    }

    private static void ValidateProfile(
        JsonElement profile,
        string expectedId,
        string expectedBaseVersion)
    {
        if (profile.ValueKind != JsonValueKind.Object)
            throw InvalidProfile("root", "an object");

        var profileId = RequireString(profile, "id");
        if (!string.Equals(profileId, expectedId, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Fabric profile id '{profileId}' does not match instance version '{expectedId}'.");

        var inherited = RequireString(profile, "inheritsFrom");
        if (!string.Equals(inherited, expectedBaseVersion, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Fabric profile inherits '{inherited}', expected Minecraft {expectedBaseVersion}.");

        if (!profile.TryGetProperty("libraries", out var libraries))
            return;
        if (libraries.ValueKind != JsonValueKind.Array)
            throw InvalidProfile("libraries", "an array");

        var index = 0;
        foreach (var library in libraries.EnumerateArray())
        {
            if (library.ValueKind != JsonValueKind.Object)
                throw InvalidProfile($"libraries[{index}]", "an object");

            MinecraftRules.Allows(library);

            ValidateOptionalString(library, "name", $"libraries[{index}].name");
            ValidateOptionalString(library, "url", $"libraries[{index}].url");

            if (library.TryGetProperty("downloads", out var downloads))
            {
                if (downloads.ValueKind != JsonValueKind.Object)
                    throw InvalidProfile($"libraries[{index}].downloads", "an object");

                if (downloads.TryGetProperty("artifact", out var artifact))
                {
                    if (artifact.ValueKind != JsonValueKind.Object)
                        throw InvalidProfile($"libraries[{index}].downloads.artifact", "an object");
                    ValidateOptionalString(
                        artifact,
                        "path",
                        $"libraries[{index}].downloads.artifact.path");
                    ValidateOptionalString(
                        artifact,
                        "url",
                        $"libraries[{index}].downloads.artifact.url");
                    ValidateOptionalString(
                        artifact,
                        "sha1",
                        $"libraries[{index}].downloads.artifact.sha1");
                }
            }

            index++;
        }
    }

    private static string RequireString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
            throw InvalidProfile(propertyName, "a non-empty string");
        return value.GetString()!;
    }

    private static void ValidateOptionalString(
        JsonElement element,
        string propertyName,
        string displayName)
    {
        if (element.TryGetProperty(propertyName, out var value)
            && value.ValueKind != JsonValueKind.String)
            throw InvalidProfile(displayName, "a string");
    }

    private static InvalidDataException InvalidProfile(string propertyName, string expected)
        => new($"Fabric profile property '{propertyName}' must be {expected}.");

    private static List<LibraryDownload> CollectLibraries(JsonElement profile, string librariesRoot)
    {
        var result = new List<LibraryDownload>();
        if (!profile.TryGetProperty("libraries", out var libraries)
            || libraries.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var library in libraries.EnumerateArray())
        {
            if (!MinecraftRules.Allows(library))
                continue;

            if (library.TryGetProperty("downloads", out var downloads)
                && downloads.TryGetProperty("artifact", out var artifact)
                && artifact.TryGetProperty("path", out var artifactPath)
                && artifact.TryGetProperty("url", out var artifactUrl))
            {
                var relative = artifactPath.GetString();
                var url = artifactUrl.GetString();
                if (string.IsNullOrWhiteSpace(relative) || string.IsNullOrWhiteSpace(url))
                    continue;
                var sha1 = artifact.TryGetProperty("sha1", out var sha) ? sha.GetString() : null;
                result.Add(new LibraryDownload(
                    url,
                    MinecraftLaunchPlanBuilder.Within(librariesRoot, relative),
                    sha1,
                    ChecksumUrl: null));
                continue;
            }

            if (!library.TryGetProperty("name", out var nameElement)
                || !library.TryGetProperty("url", out var baseUrlElement))
                continue;
            var coordinate = nameElement.GetString();
            var baseUrl = baseUrlElement.GetString();
            if (string.IsNullOrWhiteSpace(coordinate) || string.IsNullOrWhiteSpace(baseUrl))
                continue;
            var relativePath = MavenArtifactPath.FromCoordinate(coordinate);
            var urlPath = relativePath.Replace(Path.DirectorySeparatorChar, '/');
            var libraryUrl = baseUrl.TrimEnd('/') + "/" + urlPath;
            result.Add(new LibraryDownload(
                libraryUrl,
                MinecraftLaunchPlanBuilder.Within(librariesRoot, relativePath),
                Sha1: null,
                ChecksumUrl: libraryUrl + ".sha1"));
        }

        return result
            .DistinctBy(item => item.Path, OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal)
            .ToList();
    }

    private async Task DownloadLibraryAsync(
        LibraryDownload library,
        CancellationToken cancellationToken)
    {
        var expectedSha1 = library.Sha1;
        if (string.IsNullOrWhiteSpace(expectedSha1)
            && !string.IsNullOrWhiteSpace(library.ChecksumUrl))
        {
            expectedSha1 = await DownloadSha1Async(
                library.ChecksumUrl,
                cancellationToken);
        }

        if (await IsValidAsync(library.Path, expectedSha1, cancellationToken))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(library.Path)!);
        var temp = library.Path + ".part";
        TryDeleteFile(temp);
        try
        {
            using var response = await TrustedHttpDownload.SendGetAsync(
                httpClient,
                library.Url,
                "Fabric library",
                cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var output = new FileStream(
                temp,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await CopyWithIdleTimeoutAsync(
                    input,
                    output,
                    new Uri(library.Url).Host,
                    cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            if (!await IsValidAsync(temp, expectedSha1, cancellationToken))
                throw new InvalidDataException(
                    $"Downloaded Fabric library failed verification: {library.Url}");

            File.Move(temp, library.Path, overwrite: true);
        }
        catch
        {
            TryDeleteFile(temp);
            throw;
        }
    }

    private async Task<string> DownloadSha1Async(
        string url,
        CancellationToken cancellationToken)
    {
        using var response = await TrustedHttpDownload.SendGetAsync(
            httpClient,
            url,
            "Fabric library checksum",
            cancellationToken);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is > MaxChecksumBytes)
            throw new InvalidDataException(
                $"Fabric checksum response is too large: {url}");

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new MemoryStream();
        await CopyWithIdleTimeoutAsync(
            input,
            output,
            new Uri(url).Host,
            cancellationToken,
            MaxChecksumBytes);

        var text = Encoding.ASCII.GetString(output.ToArray()).Trim();
        var token = text.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (token is null
            || token.Length != 40
            || token.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException(
                $"Fabric checksum response is invalid: {url}");

        return token.ToLowerInvariant();
    }

    private async Task CopyWithIdleTimeoutAsync(
        Stream input,
        Stream output,
        string source,
        CancellationToken cancellationToken,
        long? maxBytes = null)
    {
        var buffer = new byte[128 * 1024];
        long total = 0;

        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            idle.CancelAfter(_transferIdleTimeout);

            int read;
            try
            {
                read = await input.ReadAsync(buffer.AsMemory(), idle.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Fabric transfer from {source} made no progress for {_transferIdleTimeout.TotalSeconds:0.###} seconds.");
            }

            if (read == 0)
                return;

            total = checked(total + read);
            if (maxBytes is not null && total > maxBytes.Value)
                throw new InvalidDataException(
                    $"Fabric metadata transfer from {source} exceeded the {maxBytes.Value}-byte limit.");

            await output.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);
        }
    }

    private static async Task<bool> IsValidAsync(
        string path,
        string? expectedSha1,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 0)
            return false;

        if (!string.IsNullOrWhiteSpace(expectedSha1))
        {
            await using var stream = File.OpenRead(path);
            var hash = await SHA1.HashDataAsync(stream, cancellationToken);
            return Convert.ToHexString(hash)
                .Equals(expectedSha1, StringComparison.OrdinalIgnoreCase);
        }

        return IsReadableJar(path);
    }

    private static bool IsReadableJar(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            return archive.Entries.Any(entry => !string.IsNullOrEmpty(entry.Name));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return false;
        }
    }

    private static Task WriteProfileAtomicAsync(
        string path,
        JsonElement profile,
        CancellationToken cancellationToken)
        => AtomicJsonFile.WriteAsync(
            path,
            profile,
            new JsonSerializerOptions { WriteIndented = true },
            cancellationToken);

    private static void Report(IProgress<InstallProgress>? progress, InstallProgress value)
        => progress?.Report(value);

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private sealed record LibraryDownload(
        string Url,
        string Path,
        string? Sha1,
        string? ChecksumUrl);
}
