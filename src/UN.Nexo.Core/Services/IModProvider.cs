using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public interface IModProvider
{
    string ProviderId { get; }
    string DisplayName { get; }

    Task<IReadOnlyList<ModProviderProject>> SearchAsync(
        string query,
        string minecraftVersion,
        string loader,
        int limit = 20,
        CancellationToken cancellationToken = default);

    Task<ModProviderVersion?> GetLatestCompatibleVersionAsync(
        string projectId,
        string minecraftVersion,
        string loader,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, ModProviderInstalledMatch>> MatchInstalledAsync(
        string modsDirectory,
        IReadOnlyList<InstalledMod> installedMods,
        CancellationToken cancellationToken = default);

    Task<ModProviderInstallResult> InstallAsync(
        string instanceId,
        ModProviderProject project,
        ModProviderVersion version,
        ModProviderInstalledMatch? existing,
        InstanceModService modService,
        CancellationToken cancellationToken = default);
}

public interface IModRecommendationProvider : IModProvider
{
    Task<IReadOnlyList<ModProviderRecommendation>> RecommendAsync(
        string minecraftVersion,
        string loader,
        int limit = 20,
        CancellationToken cancellationToken = default);
}
