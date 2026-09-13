using System.Text.Json;
using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed class MinecraftVersionManifestService
{
    private readonly HttpClient _httpClient;
    private readonly DownloadSourceService _downloadSources;

    public MinecraftVersionManifestService(HttpClient httpClient, DownloadSourceService downloadSources)
    {
        _httpClient = httpClient;
        _downloadSources = downloadSources;
    }

    public async Task<MinecraftVersionCatalog> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        Exception? lastException = null;

        foreach (var url in _downloadSources.GetManifestCandidates())
        {
            try
            {
                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    lastException = new HttpRequestException($"HTTP {(int)response.StatusCode} from {new Uri(url).Host}.");
                    continue;
                }

                return await ParseCatalogAsync(response, cancellationToken);
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
                or InvalidOperationException)
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
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var root = document.RootElement;
        if (!root.TryGetProperty("latest", out var latestElement)
            || !root.TryGetProperty("versions", out var versionsElement)
            || versionsElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Version catalog is missing required fields.");

        var latestRelease = latestElement.TryGetProperty("release", out var releaseElement)
            ? releaseElement.GetString()
            : null;
        var latestSnapshot = latestElement.TryGetProperty("snapshot", out var snapshotElement)
            ? snapshotElement.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(latestRelease) || string.IsNullOrWhiteSpace(latestSnapshot))
            throw new InvalidDataException("Version catalog latest release/snapshot values are invalid.");

        var latest = new MinecraftReleaseInfo(latestRelease, latestSnapshot);
        var versions = new List<MinecraftVersionInfo>();

        foreach (var item in versionsElement.EnumerateArray())
        {
            versions.Add(new MinecraftVersionInfo(
                item.GetProperty("id").GetString() ?? throw new InvalidDataException("Version id is missing."),
                item.GetProperty("type").GetString() ?? "unknown",
                item.GetProperty("url").GetString() ?? throw new InvalidDataException("Version metadata URL is missing."),
                item.GetProperty("releaseTime").GetDateTimeOffset(),
                item.GetProperty("time").GetDateTimeOffset(),
                item.TryGetProperty("sha1", out var sha1) ? sha1.GetString() ?? string.Empty : string.Empty,
                item.TryGetProperty("complianceLevel", out var compliance) ? compliance.GetInt32() : 0));
        }

        return new MinecraftVersionCatalog(latest, versions);
    }
}
