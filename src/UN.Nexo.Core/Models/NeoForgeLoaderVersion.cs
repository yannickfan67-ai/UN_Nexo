namespace UN.Nexo.Core.Models;

public sealed record NeoForgeLoaderVersion(
    string Version,
    bool IsLatest,
    bool IsPrerelease)
{
    public string DisplayName => IsLatest
        ? $"{Version} · latest compatible"
        : IsPrerelease
            ? $"{Version} · prerelease"
            : Version;
}
