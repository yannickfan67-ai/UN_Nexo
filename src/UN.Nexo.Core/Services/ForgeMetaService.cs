using System.Text.Json;
using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed class ForgeMetaService(HttpClient httpClient)
{
    public const string PromotionsUrl =
        "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";
    public const string MavenBaseUrl =
        "https://maven.minecraftforge.net/net/minecraftforge/forge";

    public async Task<IReadOnlyList<ForgeLoaderVersion>> GetVersionsAsync(
        string minecraftVersion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(minecraftVersion))
            throw new ArgumentException("Minecraft version is required.", nameof(minecraftVersion));

        using var response = await httpClient.GetAsync(PromotionsUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("promos", out var promos)
            || promos.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Forge promotions response has no promos object.");

        var mc = minecraftVersion.Trim();
        var latest = promos.TryGetProperty(mc + "-latest", out var latestElement)
            ? latestElement.GetString()
            : null;
        var recommended = promos.TryGetProperty(mc + "-recommended", out var recommendedElement)
            ? recommendedElement.GetString()
            : null;

        var versions = new List<ForgeLoaderVersion>();
        if (!string.IsNullOrWhiteSpace(recommended))
            versions.Add(new ForgeLoaderVersion(recommended, IsRecommended: true,
                IsLatest: string.Equals(recommended, latest, StringComparison.Ordinal)));
        if (!string.IsNullOrWhiteSpace(latest)
            && !string.Equals(latest, recommended, StringComparison.Ordinal))
            versions.Add(new ForgeLoaderVersion(latest, IsRecommended: false, IsLatest: true));
        return versions;
    }

    public static string GetInstallerUrl(string minecraftVersion, string forgeVersion)
    {
        ValidateVersion(minecraftVersion, nameof(minecraftVersion));
        ValidateVersion(forgeVersion, nameof(forgeVersion));
        var coordinate = $"{minecraftVersion}-{forgeVersion}";
        return $"{MavenBaseUrl}/{coordinate}/forge-{coordinate}-installer.jar";
    }

    public static string GetLaunchVersionId(string minecraftVersion, string forgeVersion)
    {
        ValidateVersion(minecraftVersion, nameof(minecraftVersion));
        ValidateVersion(forgeVersion, nameof(forgeVersion));
        return $"{minecraftVersion}-forge-{forgeVersion}";
    }

    private static void ValidateVersion(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Any(character => !(char.IsLetterOrDigit(character)
                || character is '.' or '-' or '_' or '+')))
            throw new ArgumentException("Version contains unsupported characters.", paramName);
    }
}
