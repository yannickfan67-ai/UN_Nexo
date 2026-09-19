using System.Text.Json;
using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed class ForgeMetaService(HttpClient httpClient)
{
    public const string PromotionsUrl =
        "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";
    public const string MavenBaseUrl =
        "https://maven.minecraftforge.net/net/minecraftforge/forge";
    private const int MaxMetadataBytes = 4 * 1024 * 1024;

    public async Task<IReadOnlyList<ForgeLoaderVersion>> GetVersionsAsync(
        string minecraftVersion,
        CancellationToken cancellationToken = default)
    {
        var mc = RequireVersion(
            minecraftVersion,
            nameof(minecraftVersion),
            "Minecraft version");

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

        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty(
                "promos",
                out var promos)
            || promos.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "Forge promotions response has no promos object.");
        }

        var latest = TryReadPromotion(
            promos,
            mc + "-latest");
        var recommended = TryReadPromotion(
            promos,
            mc + "-recommended");

        var versions = new List<ForgeLoaderVersion>();
        if (recommended is not null)
        {
            versions.Add(new ForgeLoaderVersion(
                recommended,
                IsRecommended: true,
                IsLatest: string.Equals(
                    recommended,
                    latest,
                    StringComparison.Ordinal)));
        }

        if (latest is not null
            && !string.Equals(
                latest,
                recommended,
                StringComparison.Ordinal))
        {
            versions.Add(new ForgeLoaderVersion(
                latest,
                IsRecommended: false,
                IsLatest: true));
        }

        return versions;
    }

    public static string GetInstallerUrl(
        string minecraftVersion,
        string forgeVersion)
    {
        var mc = RequireVersion(
            minecraftVersion,
            nameof(minecraftVersion),
            "Minecraft version");
        var forge = RequireVersion(
            forgeVersion,
            nameof(forgeVersion),
            "Forge version");
        var coordinate = $"{mc}-{forge}";
        return
            $"{MavenBaseUrl}/{coordinate}/forge-{coordinate}-installer.jar";
    }

    public static string GetLaunchVersionId(
        string minecraftVersion,
        string forgeVersion)
    {
        var mc = RequireVersion(
            minecraftVersion,
            nameof(minecraftVersion),
            "Minecraft version");
        var forge = RequireVersion(
            forgeVersion,
            nameof(forgeVersion),
            "Forge version");
        return $"{mc}-forge-{forge}";
    }

    private static string? TryReadPromotion(
        JsonElement promos,
        string key)
    {
        if (!promos.TryGetProperty(key, out var element)
            || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return RequireVersion(
            value,
            key,
            "Forge version");
    }

    private static string RequireVersion(
        string value,
        string paramName,
        string displayName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException(
                $"{displayName} is required.",
                paramName);

        var normalized = value.Trim();
        if (normalized.Any(character =>
                !(char.IsLetterOrDigit(character)
                  || character is '.' or '-' or '_' or '+')))
        {
            throw new ArgumentException(
                $"{displayName} contains unsupported characters.",
                paramName);
        }

        return normalized;
    }
}
