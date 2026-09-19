namespace UN.Nexo.Core.Models;

public sealed record InstallerLoaderVersion(
    string Version,
    bool Recommended,
    bool Latest)
{
    public string DisplayName
        => Recommended
            ? $"{Version} · recommended"
            : Latest
                ? $"{Version} · latest"
                : Version;
}
