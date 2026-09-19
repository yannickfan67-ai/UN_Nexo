using System.Text.Json;
using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed class QuiltMetaService(HttpClient httpClient)
{
    private const string BaseUrl = "https://meta.quiltmc.org";
    private const int MaxMetadataBytes = 4 * 1024 * 1024;

    public async Task<IReadOnlyList<QuiltLoaderVersion>> GetLoaderVersionsAsync(
        string minecraftVersion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(minecraftVersion))
            throw new ArgumentException(
                "Minecraft version is required.",
                nameof(minecraftVersion));

        var url =
            $"{BaseUrl}/v3/versions/loader/{Uri.EscapeDataString(minecraftVersion.Trim())}";
        using var response = await TrustedHttpDownload.SendGetAsync(
            httpClient,
            url,
            "Quilt Meta loader list",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await BoundedJsonResponse.ReadAsync(
            response.Content,
            MaxMetadataBytes,
            "Quilt Meta loader list",
            cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException(
                "Quilt loader response is not an array.");

        var versions = new List<QuiltLoaderVersion>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            JsonElement loader = item;
            if (item.TryGetProperty("loader", out var nestedLoader))
            {
                if (nestedLoader.ValueKind != JsonValueKind.Object)
                    continue;
                loader = nestedLoader;
            }

            if (!loader.TryGetProperty("version", out var versionElement)
                || versionElement.ValueKind != JsonValueKind.String)
                continue;

            var version = versionElement.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(version))
                continue;

            var stable =
                (loader.TryGetProperty("stable", out var loaderStable)
                 && loaderStable.ValueKind == JsonValueKind.True)
                || (item.TryGetProperty("stable", out var itemStable)
                    && itemStable.ValueKind == JsonValueKind.True);

            versions.Add(new QuiltLoaderVersion(version, stable));
        }

        return versions
            .DistinctBy(item => item.Version, StringComparer.Ordinal)
            .OrderByDescending(item => item.Stable)
            .ThenByDescending(
                item => item.Version,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<JsonDocument> GetProfileAsync(
        string minecraftVersion,
        string loaderVersion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(minecraftVersion))
            throw new ArgumentException(
                "Minecraft version is required.",
                nameof(minecraftVersion));
        if (string.IsNullOrWhiteSpace(loaderVersion))
            throw new ArgumentException(
                "Quilt Loader version is required.",
                nameof(loaderVersion));

        var url =
            $"{BaseUrl}/v3/versions/loader/"
            + $"{Uri.EscapeDataString(minecraftVersion.Trim())}/"
            + $"{Uri.EscapeDataString(loaderVersion.Trim())}/profile/json";
        using var response = await TrustedHttpDownload.SendGetAsync(
            httpClient,
            url,
            "Quilt Meta profile",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await BoundedJsonResponse.ReadAsync(
            response.Content,
            MaxMetadataBytes,
            "Quilt Meta profile",
            cancellationToken);
    }
}
