namespace UN.Nexo.Core.Models;

public sealed record ForgeLoaderVersion(
    string Version,
    bool IsRecommended,
    bool IsLatest)
{
    public string DisplayName => IsRecommended
        ? $"{Version} · recommended"
        : IsLatest
            ? $"{Version} · latest"
            : Version;
}
