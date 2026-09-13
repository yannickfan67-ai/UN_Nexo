namespace UN.Nexo.Core.Services;

public sealed class DownloadSourceService
{
    private const string OfficialManifest = "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";
    private const string BmclManifest = "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json";
    private const string BmclBase = "https://bmclapi2.bangbang93.com";

    public string SourceId { get; private set; } = "official";
    public string DisplayName => SourceId == "bmclapi" ? "BMCLAPI" : "Official";

    public void SetSource(string? sourceId)
    {
        SourceId = string.Equals(sourceId, "bmclapi", StringComparison.OrdinalIgnoreCase)
            ? "bmclapi"
            : "official";
    }

    public IReadOnlyList<string> GetManifestCandidates()
        => SourceId == "bmclapi"
            ? [BmclManifest, OfficialManifest]
            : [OfficialManifest];

    public IReadOnlyList<string> GetCandidates(string originalUrl)
    {
        if (SourceId != "bmclapi")
            return [originalUrl];

        var mirror = RewriteToBmcl(originalUrl);
        if (string.Equals(mirror, originalUrl, StringComparison.OrdinalIgnoreCase))
            return [originalUrl];

        return [mirror, originalUrl];
    }

    private static string RewriteToBmcl(string url)
    {
        if (TryRewrite(url, "https://resources.download.minecraft.net/", $"{BmclBase}/assets/", out var rewritten)
            || TryRewrite(url, "http://resources.download.minecraft.net/", $"{BmclBase}/assets/", out rewritten)
            || TryRewrite(url, "https://libraries.minecraft.net/", $"{BmclBase}/maven/", out rewritten)
            || TryRewrite(url, "http://libraries.minecraft.net/", $"{BmclBase}/maven/", out rewritten)
            || TryRewrite(url, "https://launchermeta.mojang.com/", $"{BmclBase}/", out rewritten)
            || TryRewrite(url, "http://launchermeta.mojang.com/", $"{BmclBase}/", out rewritten)
            || TryRewrite(url, "https://launcher.mojang.com/", $"{BmclBase}/", out rewritten)
            || TryRewrite(url, "http://launcher.mojang.com/", $"{BmclBase}/", out rewritten)
            || TryRewrite(url, "https://piston-meta.mojang.com/", $"{BmclBase}/", out rewritten)
            || TryRewrite(url, "https://piston-data.mojang.com/", $"{BmclBase}/", out rewritten))
            return rewritten;

        return url;
    }

    private static bool TryRewrite(string url, string prefix, string replacement, out string rewritten)
    {
        if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            rewritten = replacement + url[prefix.Length..];
            return true;
        }

        rewritten = url;
        return false;
    }
}
