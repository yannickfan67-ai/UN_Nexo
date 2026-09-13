namespace UN.Nexo.Core.Models;

public sealed record MinecraftVersionCatalog(
    MinecraftReleaseInfo Latest,
    IReadOnlyList<MinecraftVersionInfo> Versions);
