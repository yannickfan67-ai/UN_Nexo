using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public interface IModDependencyProvider : IModProvider
{
    Task<ModProviderProject?> GetProjectAsync(
        string projectId,
        CancellationToken cancellationToken = default);

    Task<ModProviderVersion?> GetCompatibleVersionAsync(
        string? projectId,
        string? versionId,
        string minecraftVersion,
        string loader,
        CancellationToken cancellationToken = default);

    Task<ModProviderStagedInstall> StageAsync(
        ModProviderProject project,
        ModProviderVersion version,
        CancellationToken cancellationToken = default);
}
