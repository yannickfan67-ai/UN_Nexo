namespace UN.Nexo.Core.Models;

public sealed record QuiltLoaderVersion(
    string Version,
    bool Stable)
{
    public string DisplayName
        => Stable ? $"{Version} · stable" : Version;
}
