namespace UN.Nexo.Core.Models;

public sealed record FabricLoaderVersion(string Version, bool Stable)
{
    public string DisplayName => Stable ? $"{Version} · stable" : Version;
}
