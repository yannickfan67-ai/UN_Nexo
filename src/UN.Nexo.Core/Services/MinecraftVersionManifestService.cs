using System.Text.Json;
using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed class MinecraftVersionManifestService
{
    private const string ManifestUrl = "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";
    private readonly HttpClient _httpClient;

    public MinecraftVersionManifestService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<MinecraftVersionCatalog> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(ManifestUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
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
}
