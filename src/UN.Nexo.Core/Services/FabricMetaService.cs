using System.Text.Json;
using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed class FabricMetaService(HttpClient httpClient)
{
    private const string BaseUrl = "https://meta.fabricmc.net";
    private const int MaxMetadataBytes = 4 * 1024 * 1024;

    public async Task<IReadOnlyList<FabricLoaderVersion>> GetLoaderVersionsAsync(
        string minecraftVersion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(minecraftVersion))
            throw new ArgumentException("Minecraft version is required.", nameof(minecraftVersion));

        var url = $"{BaseUrl}/v2/versions/loader/{Uri.EscapeDataString(minecraftVersion.Trim())}";
        using var response = await TrustedHttpDownload.SendGetAsync(
            httpClient,
            url,
            "Fabric Meta loader list",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await BoundedJsonResponse.ReadAsync(
            response.Content,
            MaxMetadataBytes,
            "Fabric Meta loader list",
            cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Fabric loader response is not an array.");

        var versions = new List<FabricLoaderVersion>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("loader", out var loader)
                || loader.ValueKind != JsonValueKind.Object
                || !loader.TryGetProperty("version", out var versionElement)
                || versionElement.ValueKind != JsonValueKind.String)
                continue;
            var version = versionElement.GetString();
            if (string.IsNullOrWhiteSpace(version))
                continue;
            var stable = loader.TryGetProperty("stable", out var stableElement)
                && stableElement.ValueKind == JsonValueKind.True;
            versions.Add(new FabricLoaderVersion(version, stable));
        }

        return versions
            .DistinctBy(item => item.Version, StringComparer.Ordinal)
            .OrderByDescending(item => item.Stable)
            .ThenByDescending(item => item.Version, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<JsonDocument> GetProfileAsync(
        string minecraftVersion,
        string loaderVersion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(minecraftVersion))
            throw new ArgumentException("Minecraft version is required.", nameof(minecraftVersion));
        if (string.IsNullOrWhiteSpace(loaderVersion))
            throw new ArgumentException("Fabric Loader version is required.", nameof(loaderVersion));

        var url = $"{BaseUrl}/v2/versions/loader/" +
                  $"{Uri.EscapeDataString(minecraftVersion.Trim())}/" +
                  $"{Uri.EscapeDataString(loaderVersion.Trim())}/profile/json";
        using var response = await TrustedHttpDownload.SendGetAsync(
            httpClient,
            url,
            "Fabric Meta profile",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await BoundedJsonResponse.ReadAsync(
            response.Content,
            MaxMetadataBytes,
            "Fabric Meta profile",
            cancellationToken);
    }
}
