using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed class ModrinthModProvider : IModProvider
{
    private const string ApiBase = "https://api.modrinth.com/v2/";
    private const int MaxMetadataBytes = 4 * 1024 * 1024;
    private const long MaxModBytes = 512L * 1024L * 1024L;
    private const int MaxDownloadRedirects = 5;
    private readonly HttpClient _httpClient;
    private readonly string _userAgent;

    public ModrinthModProvider(HttpClient httpClient, string? userAgent = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _userAgent = string.IsNullOrWhiteSpace(userAgent)
            ? "yannickfan67-ai-UN_Nexo/0.0 (github.com/yannickfan67-ai/UN_Nexo)"
            : userAgent.Trim();

        using var probe = new HttpRequestMessage();
        probe.Headers.UserAgent.ParseAdd(_userAgent);
    }

    public string ProviderId => "modrinth";
    public string DisplayName => "Modrinth";

    public async Task<IReadOnlyList<ModProviderProject>> SearchAsync(
        string query,
        string minecraftVersion,
        string loader,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("A Modrinth search query is required.", nameof(query));
        var version = RequireValue(minecraftVersion, nameof(minecraftVersion));
        var normalizedLoader = NormalizeLoader(loader);
        if (limit is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(limit), "Modrinth search limit must be between 1 and 100.");

        var facets = JsonSerializer.Serialize(new[]
        {
            new[] { "project_type:mod" },
            new[] { $"versions:{version}" },
            new[] { $"categories:{normalizedLoader}" }
        });
        var relative =
            "search?query=" + Uri.EscapeDataString(query.Trim()) +
            "&facets=" + Uri.EscapeDataString(facets) +
            "&limit=" + limit;

        using var request = CreateRequest(HttpMethod.Get, relative);
        using var document = await SendJsonAsync(request, cancellationToken);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("hits", out var hits)
            || hits.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Modrinth search response must contain a hits array.");

        var projects = new List<ModProviderProject>();
        foreach (var item in hits.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !TryGetRequiredString(item, "project_id", out var projectId)
                || !IsOpaqueId(projectId)
                || !TryGetRequiredString(item, "slug", out var slug)
                || !TryGetRequiredString(item, "title", out var title))
                continue;

            var description = TryGetString(item, "description") ?? string.Empty;
            var author = TryGetString(item, "author") ?? "Unknown author";
            var iconUrl = TryGetString(item, "icon_url");
            var downloads = item.TryGetProperty("downloads", out var downloadsElement)
                            && downloadsElement.TryGetInt64(out var parsedDownloads)
                ? Math.Max(0, parsedDownloads)
                : 0;

            projects.Add(new ModProviderProject(
                ProviderId,
                projectId,
                slug,
                title,
                description,
                author,
                iconUrl,
                downloads,
                "https://modrinth.com/mod/" + Uri.EscapeDataString(slug)));
        }

        return projects;
    }

    public async Task<ModProviderVersion?> GetLatestCompatibleVersionAsync(
        string projectId,
        string minecraftVersion,
        string loader,
        CancellationToken cancellationToken = default)
    {
        if (!IsOpaqueId(projectId))
            throw new ArgumentException("A valid Modrinth project id is required.", nameof(projectId));
        var version = RequireValue(minecraftVersion, nameof(minecraftVersion));
        var normalizedLoader = NormalizeLoader(loader);
        var loaders = Uri.EscapeDataString(JsonSerializer.Serialize(new[] { normalizedLoader }));
        var gameVersions = Uri.EscapeDataString(JsonSerializer.Serialize(new[] { version }));
        var relative =
            $"project/{Uri.EscapeDataString(projectId)}/version" +
            $"?loaders={loaders}&game_versions={gameVersions}&include_changelog=false";

        using var request = CreateRequest(HttpMethod.Get, relative);
        using var document = await SendJsonAsync(request, cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Modrinth project versions response must be an array.");

        ModProviderVersion? latest = null;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var candidate = ParseVersion(item);
            if (candidate is null
                || !string.Equals(candidate.ProjectId, projectId, StringComparison.Ordinal))
                continue;
            if (latest is null || candidate.PublishedAt > latest.PublishedAt)
                latest = candidate;
        }

        return latest;
    }

    public async Task<IReadOnlyDictionary<string, ModProviderInstalledMatch>> MatchInstalledAsync(
        string modsDirectory,
        IReadOnlyList<InstalledMod> installedMods,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installedMods);
        if (installedMods.Count == 0 || !Directory.Exists(modsDirectory))
            return new Dictionary<string, ModProviderInstalledMatch>(StringComparer.Ordinal);

        var byHash = new Dictionary<string, InstalledMod>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in installedMods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!InstanceModService.TryResolvePhysicalManagedModPath(
                    modsDirectory,
                    mod.FileName,
                    out var candidate))
            {
                continue;
            }

            await using var stream = new FileStream(
                candidate,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            // Revalidate after open. If a regular candidate was replaced by a
            // symlink/reparse point before the handle opened, do not hash it.
            if (!InstanceModService.TryResolvePhysicalManagedModPath(
                    modsDirectory,
                    mod.FileName,
                    out var revalidated)
                || !PathEquals(candidate, revalidated))
            {
                continue;
            }

            using var sha1 = SHA1.Create();
            var hash = await sha1.ComputeHashAsync(stream, cancellationToken);
            byHash[Convert.ToHexString(hash).ToLowerInvariant()] = mod;
        }

        if (byHash.Count == 0)
            return new Dictionary<string, ModProviderInstalledMatch>(StringComparer.Ordinal);

        using var request = CreateRequest(HttpMethod.Post, "version_files");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { hashes = byHash.Keys.ToArray(), algorithm = "sha1" }),
            Encoding.UTF8,
            "application/json");

        using var document = await SendJsonAsync(request, cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Modrinth version-files response must be an object.");

        var result = new Dictionary<string, ModProviderInstalledMatch>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!byHash.TryGetValue(property.Name, out var local)
                || property.Value.ValueKind != JsonValueKind.Object
                || !TryGetRequiredString(property.Value, "project_id", out var matchedProjectId)
                || !IsOpaqueId(matchedProjectId)
                || !TryGetRequiredString(property.Value, "id", out var matchedVersionId)
                || !IsOpaqueId(matchedVersionId))
                continue;

            var versionNumber = TryGetString(property.Value, "version_number") ?? matchedVersionId;
            result.TryAdd(
                matchedProjectId,
                new ModProviderInstalledMatch(
                    ProviderId,
                    matchedProjectId,
                    matchedVersionId,
                    versionNumber,
                    local.FileName,
                    local.IsEnabled));
        }

        return result;
    }

    public async Task<ModProviderInstallResult> InstallAsync(
        string instanceId,
        ModProviderProject project,
        ModProviderVersion version,
        ModProviderInstalledMatch? existing,
        InstanceModService modService,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            throw new ArgumentException("An instance id is required.", nameof(instanceId));
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(modService);
        if (!string.Equals(project.ProviderId, ProviderId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(version.ProviderId, ProviderId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(project.ProjectId, version.ProjectId, StringComparison.Ordinal))
            throw new InvalidDataException("The selected Modrinth project and version do not match.");
        if (existing is not null
            && (!string.Equals(existing.ProviderId, ProviderId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(existing.ProjectId, project.ProjectId, StringComparison.Ordinal)))
            throw new InvalidDataException("The installed Modrinth match does not belong to the selected project.");

        var file = version.SelectPrimaryFile();
        ValidateProviderFile(file);

        var stagingRoot = Path.Combine(
            Path.GetTempPath(),
            "UN_Nexo",
            "modrinth",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingRoot);
        var stagedPath = Path.Combine(stagingRoot, file.FileName);

        try
        {
            await DownloadVerifiedAsync(file, stagedPath, cancellationToken);
            var installed = await modService.InstallProviderUpdateAsync(
                instanceId,
                stagedPath,
                existing?.LocalFileName,
                existing?.IsEnabled ?? true,
                cancellationToken);

            return new ModProviderInstallResult(project, version, installed);
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingRoot))
                    Directory.Delete(stagingRoot, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static bool PathEquals(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private async Task DownloadVerifiedAsync(
        ModProviderFile file,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        using var response = await SendDownloadAsync(file.DownloadUrl, cancellationToken);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is { } declaredLength)
        {
            if (declaredLength > MaxModBytes)
                throw new InvalidDataException("The Modrinth file is larger than Nexo's download safety limit.");
            if (declaredLength != file.Size)
                throw new InvalidDataException("The Modrinth file size does not match its version metadata.");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = new byte[128 * 1024];
        long total = 0;

        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;
            total += read;
            if (total > MaxModBytes || total > file.Size)
                throw new InvalidDataException("The Modrinth download exceeded its declared safe size.");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        await output.FlushAsync(cancellationToken);

        if (total != file.Size)
            throw new InvalidDataException("The Modrinth download ended before the declared file size.");
        var actualSha1 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (!string.Equals(actualSha1, file.Sha1, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Modrinth download failed SHA-1 verification.");
    }

    private async Task<HttpResponseMessage> SendDownloadAsync(
        string downloadUrl,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var currentUri)
            || !IsTrustedDownloadUri(currentUri))
            throw new InvalidDataException("The selected Modrinth download URL is not a trusted CDN origin.");

        for (var redirectCount = 0; ; redirectCount++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            ApplyHeaders(request);

            var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            var effectiveUri = response.RequestMessage?.RequestUri;
            if (effectiveUri is not null && !effectiveUri.Equals(currentUri))
            {
                response.Dispose();
                throw new InvalidDataException(
                    "The Modrinth HTTP client followed a redirect automatically. Automatic redirects must be disabled so Nexo can validate every redirect target before connecting.");
            }

            if (!IsRedirectStatusCode(response.StatusCode))
                return response;

            if (redirectCount >= MaxDownloadRedirects)
            {
                response.Dispose();
                throw new InvalidDataException(
                    $"The Modrinth download exceeded the {MaxDownloadRedirects}-redirect safety limit.");
            }

            var location = response.Headers.Location;
            if (location is null)
            {
                response.Dispose();
                throw new InvalidDataException("The Modrinth download returned a redirect without a Location header.");
            }

            Uri nextUri;
            try
            {
                nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
            }
            catch (UriFormatException ex)
            {
                response.Dispose();
                throw new InvalidDataException("The Modrinth download returned an invalid redirect target.", ex);
            }

            response.Dispose();
            if (!IsTrustedDownloadUri(nextUri))
                throw new InvalidDataException(
                    "The Modrinth download redirect left the trusted HTTPS CDN origin.");

            currentUri = nextUri;
        }
    }

    private ModProviderVersion? ParseVersion(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object
            || !TryGetRequiredString(item, "id", out var versionId)
            || !IsOpaqueId(versionId)
            || !TryGetRequiredString(item, "project_id", out var projectId)
            || !IsOpaqueId(projectId)
            || !TryGetRequiredString(item, "version_number", out var versionNumber)
            || !TryGetRequiredString(item, "date_published", out var publishedText)
            || !DateTimeOffset.TryParse(publishedText, out var published)
            || !item.TryGetProperty("files", out var filesElement)
            || filesElement.ValueKind != JsonValueKind.Array)
            return null;

        var files = new List<ModProviderFile>();
        foreach (var fileElement in filesElement.EnumerateArray())
        {
            var file = ParseFile(fileElement);
            if (file is not null)
                files.Add(file);
        }
        if (files.Count == 0)
            return null;

        var name = TryGetString(item, "name") ?? versionNumber;
        return new ModProviderVersion(
            ProviderId,
            projectId,
            versionId,
            name,
            versionNumber,
            published,
            files);
    }

    private static ModProviderFile? ParseFile(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object
            || !TryGetRequiredString(item, "filename", out var fileName)
            || !IsSafeJarFileName(fileName)
            || !TryGetRequiredString(item, "url", out var url)
            || !IsTrustedDownloadUrl(url)
            || !item.TryGetProperty("size", out var sizeElement)
            || !sizeElement.TryGetInt64(out var size)
            || size is <= 0 or > MaxModBytes
            || !item.TryGetProperty("hashes", out var hashes)
            || hashes.ValueKind != JsonValueKind.Object
            || !TryGetRequiredString(hashes, "sha1", out var sha1)
            || !IsSha1(sha1))
            return null;

        if (item.TryGetProperty("file_type", out var fileType)
            && fileType.ValueKind == JsonValueKind.String
            && fileType.GetString() is "sources-jar" or "dev-jar" or "javadoc-jar" or "signature")
            return null;

        var primary = item.TryGetProperty("primary", out var primaryElement)
                      && primaryElement.ValueKind == JsonValueKind.True;
        return new ModProviderFile(fileName, url, sha1.ToLowerInvariant(), size, primary);
    }

    private async Task<JsonDocument> SendJsonAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ApplyHeaders(request);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is > MaxMetadataBytes)
            throw new InvalidDataException("Modrinth metadata response is too large.");

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(chunk, cancellationToken);
            if (read == 0)
                break;
            if (buffer.Length + read > MaxMetadataBytes)
                throw new InvalidDataException("Modrinth metadata response exceeded the size limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        buffer.Position = 0;

        try
        {
            return await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Modrinth returned malformed JSON metadata.", ex);
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string relative)
        => new(method, new Uri(new Uri(ApiBase), relative));

    private void ApplyHeaders(HttpRequestMessage request)
    {
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.ParseAdd(_userAgent);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static string NormalizeLoader(string loader)
    {
        var value = RequireValue(loader, nameof(loader)).ToLowerInvariant();
        return value switch
        {
            "fabric" or "forge" or "neoforge" or "quilt" => value,
            _ => throw new NotSupportedException($"Modrinth mod installation is not supported for loader '{loader}'.")
        };
    }

    private static string RequireValue(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A value is required.", parameterName);
        return value.Trim();
    }

    private static bool TryGetRequiredString(JsonElement element, string name, out string value)
    {
        value = TryGetString(element, name) ?? string.Empty;
        return value.Length > 0;
    }

    private static string? TryGetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    private static bool IsOpaqueId(string value)
        => value.Length is > 0 and <= 64
           && value.All(character => char.IsAsciiLetterOrDigit(character));

    private static bool IsSha1(string value)
        => value.Length == 40 && value.All(Uri.IsHexDigit);

    private static bool IsSafeJarFileName(string fileName)
        => fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
           && fileName is not "." and not ".."
           && string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
           && fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static bool IsTrustedDownloadUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && IsTrustedDownloadUri(uri);

    private static bool IsTrustedDownloadUri(Uri uri)
        => uri.Scheme == Uri.UriSchemeHttps
           && (uri.IsDefaultPort || uri.Port == 443)
           && string.IsNullOrEmpty(uri.UserInfo)
           && string.Equals(uri.Host, "cdn.modrinth.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsRedirectStatusCode(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static void ValidateProviderFile(ModProviderFile file)
    {
        if (!IsSafeJarFileName(file.FileName)
            || !IsTrustedDownloadUrl(file.DownloadUrl)
            || !IsSha1(file.Sha1)
            || file.Size is <= 0 or > MaxModBytes)
            throw new InvalidDataException("The selected Modrinth file metadata is not safe to install.");
    }

    private static bool FileNameEquals(string left, string right)
        => string.Equals(
            left,
            right,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
