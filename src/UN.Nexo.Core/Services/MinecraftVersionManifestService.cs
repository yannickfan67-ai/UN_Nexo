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

    public async Task<MinecraftReleaseInfo> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(ManifestUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var latest = document.RootElement.GetProperty("latest");
        return new MinecraftReleaseInfo(
            latest.GetProperty("release").GetString() ?? "Unknown",
            latest.GetProperty("snapshot").GetString() ?? "Unknown");
    }
}
