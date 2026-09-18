using System.Globalization;
using System.Text.Json;
using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed class MinecraftVersionManifestService
{
    private const int MaxCatalogBytes = 16 * 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly DownloadSourceService _downloadSources;
    private readonly TimeSpan _bodyIdleTimeout;

    public MinecraftVersionManifestService(
        HttpClient httpClient,
        DownloadSourceService downloadSources,
        TimeSpan? bodyIdleTimeout = null)
    {
        _httpClient = httpClient;
        _downloadSources = downloadSources;
        _bodyIdleTimeout = bodyIdleTimeout ?? TimeSpan.FromSeconds(30);
        if (_bodyIdleTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(bodyIdleTimeout),
                "Version catalog body idle timeout must be positive.");
    }

    public async Task<MinecraftVersionCatalog> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        Exception? lastException = null;

        foreach (var url in _downloadSources.GetManifestCandidates())
        {
            try
            {
                using var response = await TrustedHttpDownload.SendGetAsync(
                    _httpClient,
                    url,
                    "Minecraft version catalog",
                    cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    lastException = new HttpRequestException($"HTTP {(int)response.StatusCode} from {new Uri(url).Host}.");
                    continue;
                }

                return await ParseCatalogAsync(
                    response,
                    _bodyIdleTimeout,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastException = new HttpRequestException($"Timed out while reading the version catalog from {new Uri(url).Host}.");
            }
            catch (Exception ex) when (ex is HttpRequestException
                or IOException
                or JsonException
                or InvalidDataException
                or KeyNotFoundException
                or FormatException
                or InvalidOperationException
                or TimeoutException)
            {
                lastException = ex;
            }
        }

        throw new HttpRequestException("No download source returned a valid Minecraft version catalog.", lastException);
    }

    public async Task<MinecraftReleaseInfo> GetLatestAsync(CancellationToken cancellationToken = default)
        => (await GetCatalogAsync(cancellationToken)).Latest;

    private static async Task<MinecraftVersionCatalog> ParseCatalogAsync(
        HttpResponseMessage response,
        TimeSpan bodyIdleTimeout,
        CancellationToken cancellationToken)
    {
        using var document = await BoundedJsonResponse.ReadAsync(
            response.Content,
            MaxCatalogBytes,
            "Minecraft version catalog",
            cancellationToken,
            bodyIdleTimeout);

        var root = document.RootElement;
        RequireObject(root, "root");
        if (!root.TryGetProperty("latest", out var latestElement))
            throw InvalidCatalog("latest", "an object");
        RequireObject(latestElement, "latest");

        var latestRelease = RequireString(latestElement, "release", "latest.release");
        var latestSnapshot = RequireString(latestElement, "snapshot", "latest.snapshot");

        if (!root.TryGetProperty("versions", out var versionsElement)
            || versionsElement.ValueKind != JsonValueKind.Array)
            throw InvalidCatalog("versions", "an array");

        var latest = new MinecraftReleaseInfo(latestRelease, latestSnapshot);
        var versions = new List<MinecraftVersionInfo>();
        var index = 0;

        foreach (var item in versionsElement.EnumerateArray())
        {
            RequireObject(item, $"versions[{index}]");

            var id = RequireString(item, "id", $"versions[{index}].id");
            var type = RequireString(item, "type", $"versions[{index}].type");
            var url = RequireString(item, "url", $"versions[{index}].url");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var metadataUri)
                || metadataUri.Scheme is not ("https" or "http"))
                throw new InvalidDataException(
                    $"Version catalog property 'versions[{index}].url' must be an absolute HTTP(S) URL.");

            var releaseTime = RequireDateTimeOffset(
                item,
                "releaseTime",
                $"versions[{index}].releaseTime");
            var time = RequireDateTimeOffset(
                item,
                "time",
                $"versions[{index}].time");

            var sha1 = OptionalString(
                item,
                "sha1",
                $"versions[{index}].sha1") ?? string.Empty;
            var compliance = OptionalInt32(
                item,
                "complianceLevel",
                $"versions[{index}].complianceLevel") ?? 0;

            versions.Add(new MinecraftVersionInfo(
                id,
                type,
                metadataUri.ToString(),
                releaseTime,
                time,
                sha1,
                compliance));
            index++;
        }

        return new MinecraftVersionCatalog(latest, versions);
    }

    private static void RequireObject(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw InvalidCatalog(propertyName, "an object");
    }

    private static string RequireString(
        JsonElement element,
        string propertyName,
        string displayName)
    {
        if (!element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
            throw InvalidCatalog(displayName, "a non-empty string");

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
            throw InvalidCatalog(displayName, "a string");
        return value.GetString();
    }

    private static int? OptionalInt32(
        JsonElement element,
        string propertyName,
        string displayName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var result))
            throw InvalidCatalog(displayName, "a 32-bit integer");
        return result;
    }

    private static DateTimeOffset RequireDateTimeOffset(
        JsonElement element,
        string propertyName,
        string displayName)
    {
        var text = RequireString(element, propertyName, displayName);
        if (!DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var value))
            throw InvalidCatalog(displayName, "an ISO-8601 date/time string");
        return value;
    }

    private static InvalidDataException InvalidCatalog(
        string propertyName,
        string expected)
        => new($"Version catalog property '{propertyName}' must be {expected}.");

}
