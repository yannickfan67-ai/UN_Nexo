using System.Security.Cryptography;
using System.Text.Json;
using UN.Nexo.Core.Launching;
using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed class FabricInstallService(
    HttpClient httpClient,
    NexoPathService paths,
    MinecraftVanillaInstallService vanillaInstaller,
    FabricMetaService fabricMeta)
{
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
            var profileId = profile.RootElement.GetProperty("id").GetString();
            if (!string.Equals(profileId, instance.VersionId, StringComparison.Ordinal))
            {
                profile.Dispose();
                throw new InvalidDataException(
                    $"Fabric profile id '{profileId}' does not match instance version '{instance.VersionId}'.");
            }
            await WriteProfileAtomicAsync(profilePath, profile.RootElement, cancellationToken);
        }

        using (profile)
        {
            var root = profile.RootElement;
            var inherited = root.TryGetProperty("inheritsFrom", out var inherits)
                ? inherits.GetString()
                : null;
            if (!string.Equals(inherited, baseVersion.Id, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Fabric profile inherits '{inherited}', expected Minecraft {baseVersion.Id}.");

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
                await WriteJsonAtomicAsync(statePath, state, cancellationToken);
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
        return document.RootElement.TryGetProperty("inheritsFrom", out var inherits)
            ? inherits.GetString()
            : null;
    }

    public static string? ReadLoaderVersion(JsonElement profile)
    {
        if (!profile.TryGetProperty("libraries", out var libraries)
            || libraries.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var library in libraries.EnumerateArray())
        {
            if (!library.TryGetProperty("name", out var nameElement))
                continue;
            var name = nameElement.GetString();
            const string prefix = "net.fabricmc:fabric-loader:";
            if (name is not null && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return name[prefix.Length..];
        }
        return null;
    }

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
                    Path.Combine(librariesRoot, relative.Replace('/', Path.DirectorySeparatorChar)),
                    sha1));
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
            result.Add(new LibraryDownload(
                baseUrl.TrimEnd('/') + "/" + urlPath,
                Path.Combine(librariesRoot, relativePath),
                null));
        }

        return result
            .DistinctBy(item => item.Path, OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal)
            .ToList();
    }

    private async Task DownloadLibraryAsync(LibraryDownload library, CancellationToken cancellationToken)
    {
        if (await IsValidAsync(library.Path, library.Sha1, cancellationToken))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(library.Path)!);
        var temp = library.Path + ".part";
        TryDeleteFile(temp);
        try
        {
            using var response = await httpClient.GetAsync(
                library.Url,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(
                temp,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);
            output.Close();

            if (!await IsValidAsync(temp, library.Sha1, cancellationToken))
                throw new InvalidDataException($"Downloaded Fabric library failed verification: {library.Url}");
            File.Move(temp, library.Path, overwrite: true);
        }
        catch
        {
            TryDeleteFile(temp);
            throw;
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
        if (string.IsNullOrWhiteSpace(expectedSha1))
            return true;

        await using var stream = File.OpenRead(path);
        var hash = await SHA1.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).Equals(expectedSha1, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WriteProfileAtomicAsync(
        string path,
        JsonElement profile,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(
                temp,
                JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDeleteFile(temp);
            throw;
        }
    }

    private static async Task WriteJsonAtomicAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(
                temp,
                JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDeleteFile(temp);
            throw;
        }
    }

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

    private sealed record LibraryDownload(string Url, string Path, string? Sha1);
}
