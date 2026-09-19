using System.Text.Json;
using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed class ForgeMetaService(HttpClient httpClient)
{
    private const string PromotionsUrl =
        "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";
    private const string MavenBase =
        "https://maven.minecraftforge.net/net/minecraftforge/forge";
    private const int MaxMetadataBytes = 4 * 1024 * 1024;

    public async Task<IReadOnlyList<InstallerLoaderVersion>> GetVersionsAsync(
        string minecraftVersion,
        CancellationToken cancellationToken = default)
    {
        var gameVersion = RequireValue(
            minecraftVersion,
            nameof(minecraftVersion));
        using var response = await TrustedHttpDownload.SendGetAsync(
            httpClient,
            PromotionsUrl,
            "Forge promotions",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await BoundedJsonResponse.ReadAsync(
            response.Content,
            MaxMetadataBytes,
            "Forge promotions",
            cancellationToken);

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("promos", out var promos)
            || promos.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "Forge promotions response must contain a promos object.");
        }

        var recommended = ReadPromotion(
            promos,
            gameVersion + "-recommended");
        var latest = ReadPromotion(
            promos,
            gameVersion + "-latest");
        var values = new List<InstallerLoaderVersion>(2);

        if (!string.IsNullOrWhiteSpace(recommended))
        {
            values.Add(new InstallerLoaderVersion(
                recommended!,
                Recommended: true,
                Latest: string.Equals(
                    recommended,
                    latest,
                    StringComparison.Ordinal)));
        }

        if (!string.IsNullOrWhiteSpace(latest)
            && !values.Any(item =>
                item.Version.Equals(
                    latest,
                    StringComparison.Ordinal)))
        {
            values.Add(new InstallerLoaderVersion(
                latest!,
                Recommended: false,
                Latest: true));
        }

        return values;
    }

    public static string GetLaunchVersionId(
        string minecraftVersion,
        string forgeVersion)
        => $"{RequireValue(minecraftVersion, nameof(minecraftVersion))}-forge-{RequireValue(forgeVersion, nameof(forgeVersion))}";

    public static string GetInstallerUrl(
        string minecraftVersion,
        string forgeVersion)
    {
        var game = RequireValue(
            minecraftVersion,
            nameof(minecraftVersion));
        var loader = RequireValue(
            forgeVersion,
            nameof(forgeVersion));
        var combined = game + "-" + loader;
        return $"{MavenBase}/{combined}/forge-{combined}-installer.jar";
    }

    private static string? ReadPromotion(
        JsonElement promos,
        string name)
        => promos.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    private static string RequireValue(
        string value,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException(
                "A value is required.",
                parameterName);
        return value.Trim();
    }
}
