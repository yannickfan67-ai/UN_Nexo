namespace UN.Nexo.Core.Services;

internal static class TrustedDownloadPolicy
{
    private static readonly HashSet<string> TrustedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "launchermeta.mojang.com",
        "launcher.mojang.com",
        "piston-meta.mojang.com",
        "piston-data.mojang.com",
        "resources.download.minecraft.net",
        "libraries.minecraft.net",
        "assets.minecraft.net",
        "meta.fabricmc.net",
        "maven.fabricmc.net",
        "meta.quiltmc.org",
        "maven.quiltmc.org",
        "bmclapi2.bangbang93.com"
    };

    internal static Uri RequireTrustedUri(string value, string label)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            throw new InvalidDataException($"{label} is not a valid absolute URL.");

        uri = UpgradeKnownHttpOrigin(uri);
        if (!IsTrustedUri(uri))
            throw new InvalidDataException(
                $"{label} must use a trusted HTTPS Minecraft/Fabric download origin.");

        return uri;
    }

    internal static bool IsTrustedUri(Uri uri)
        => uri.Scheme == Uri.UriSchemeHttps
           && (uri.IsDefaultPort || uri.Port == 443)
           && string.IsNullOrEmpty(uri.UserInfo)
           && TrustedHosts.Contains(uri.IdnHost);

    private static Uri UpgradeKnownHttpOrigin(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttp
            || (!uri.IsDefaultPort && uri.Port != 80)
            || !TrustedHosts.Contains(uri.IdnHost))
            return uri;

        var builder = new UriBuilder(uri)
        {
            Scheme = Uri.UriSchemeHttps,
            Port = -1
        };
        return builder.Uri;
    }
}
