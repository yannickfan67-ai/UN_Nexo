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
        using var response = await GetFirstSuccessfulAsync(_downloadSources.GetManifestCandidates(), cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var root = document.RootElement;
        var latestElement = root.GetProperty("latest");
        var latest = new MinecraftReleaseInfo(
            latestElement.GetProperty("release").GetString() ?? "Unknown",
            latestElement.GetProperty("snapshot").GetString() ?? "Unknown");

        var versions = new List<MinecraftVersionInfo>();
        foreach (var item in root.GetProperty("versions").EnumerateArray())
        {
            versions.Add(new MinecraftVersionInfo(
                item.GetProperty("id").GetString() ?? "Unknown",
                item.GetProperty("type").GetString() ?? "unknown",
                item.GetProperty("url").GetString() ?? string.Empty,
                item.GetProperty("releaseTime").GetDateTimeOffset(),
                item.GetProperty("time").GetDateTimeOffset(),
                item.TryGetProperty("sha1", out var sha1) ? sha1.GetString() ?? string.Empty : string.Empty,
                item.TryGetProperty("complianceLevel", out var compliance) ? compliance.GetInt32() : 0));
        }

        return new MinecraftVersionCatalog(latest, versions);
    }

    public async Task<MinecraftReleaseInfo> GetLatestAsync(CancellationToken cancellationToken = default)
        => (await GetCatalogAsync(cancellationToken)).Latest;

    private async Task<HttpResponseMessage> GetFirstSuccessfulAsync(
        IReadOnlyList<string> urls,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        foreach (var url in urls)
        {
            try
            {
                var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.IsSuccessStatusCode)
                    return response;

                lastException = new HttpRequestException($"HTTP {(int)response.StatusCode} from {new Uri(url).Host}.");
                response.Dispose();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastException = ex;
            }
        }

        throw lastException ?? new HttpRequestException("No download source was available.");
    }
}
