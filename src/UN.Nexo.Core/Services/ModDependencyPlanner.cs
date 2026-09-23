using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed class ModDependencyPlanner
{
    private const int MaxDependencyProjects = 128;
    private readonly IModDependencyProvider _provider;

    public ModDependencyPlanner(IModDependencyProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public async Task<ModDependencyPlan> BuildAsync(
        ModProviderProject rootProject,
        ModProviderVersion rootVersion,
        string minecraftVersion,
        string loader,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rootProject);
        ArgumentNullException.ThrowIfNull(rootVersion);

        ValidateNodeIdentity(rootProject, rootVersion);

        var version = RequireValue(minecraftVersion, nameof(minecraftVersion));
        var normalizedLoader = RequireValue(loader, nameof(loader));
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var selected = new Dictionary<string, ModDependencyPlanEntry>(StringComparer.Ordinal);
        var ordered = new List<ModDependencyPlanEntry>();
        var optional = new List<ModProviderDependency>();
        var incompatibleEdges = new List<ModProviderIncompatibility>();

        await VisitAsync(rootProject, rootVersion, isRoot: true);

        foreach (var edge in incompatibleEdges)
        {
            if (selected.Values.Any(item => MatchesIncompatibility(edge.Dependency, item.Version)))
            {
                throw new InvalidDataException(
                    $"Dependency conflict: '{edge.SourceTitle}' ({edge.SourceProjectId}) declares another project/version selected by this install plan as incompatible.");
            }
        }

        return new ModDependencyPlan(
            ordered,
            optional
                .GroupBy(item => (item.ProjectId, item.VersionId, item.Type))
                .Select(group => group.First())
                .ToArray(),
            incompatibleEdges
                .GroupBy(item => (item.SourceProjectId, item.Dependency.ProjectId, item.Dependency.VersionId))
                .Select(group => group.First())
                .ToArray());

        async Task VisitAsync(ModProviderProject project, ModProviderVersion candidateVersion, bool isRoot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateNodeIdentity(project, candidateVersion);

            if (selected.TryGetValue(project.ProjectId, out var existing))
            {
                if (!string.Equals(existing.Version.VersionId, candidateVersion.VersionId, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Dependency conflict: project '{project.ProjectId}' requires multiple versions ('{existing.Version.VersionId}' and '{candidateVersion.VersionId}').");
                }
                return;
            }

            if (!visiting.Add(project.ProjectId))
                throw new InvalidDataException($"Dependency cycle detected at project '{project.ProjectId}'.");

            if (selected.Count + visiting.Count > MaxDependencyProjects)
                throw new InvalidDataException($"The dependency graph exceeds the {MaxDependencyProjects}-project safety limit.");

            try
            {
                foreach (var dependency in candidateVersion.Dependencies)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    switch (dependency.Type)
                    {
                        case ModProviderDependencyType.Required:
                        {
                            if (dependency.ProjectId is null && dependency.VersionId is null)
                                throw new InvalidDataException(
                                    $"Required dependency from '{project.ProjectId}' has neither project nor version id.");

                            var dependencyVersion = await _provider.GetCompatibleVersionAsync(
                                dependency.ProjectId,
                                dependency.VersionId,
                                version,
                                normalizedLoader,
                                cancellationToken)
                                ?? throw new InvalidDataException(
                                    $"Required dependency from '{project.Title}' has no compatible version for Minecraft {version} / {normalizedLoader}.");

                            if (!string.Equals(
                                    dependencyVersion.ProviderId,
                                    _provider.ProviderId,
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                throw new InvalidDataException(
                                    $"Required dependency version '{dependencyVersion.VersionId}' belongs to unexpected provider '{dependencyVersion.ProviderId}'.");
                            }

                            if (dependency.VersionId is not null
                                && !string.Equals(
                                    dependency.VersionId,
                                    dependencyVersion.VersionId,
                                    StringComparison.Ordinal))
                            {
                                throw new InvalidDataException(
                                    $"Required dependency requested version '{dependency.VersionId}', but provider returned '{dependencyVersion.VersionId}'.");
                            }

                            var dependencyProjectId = dependencyVersion.ProjectId;
                            if (dependency.ProjectId is not null
                                && !string.Equals(dependency.ProjectId, dependencyProjectId, StringComparison.Ordinal))
                            {
                                throw new InvalidDataException(
                                    $"Required dependency version '{dependencyVersion.VersionId}' belongs to unexpected project '{dependencyProjectId}'.");
                            }

                            if (visiting.Contains(dependencyProjectId))
                                throw new InvalidDataException(
                                    $"Dependency cycle detected: '{project.ProjectId}' -> '{dependencyProjectId}'.");

                            if (selected.TryGetValue(dependencyProjectId, out var selectedDependency))
                            {
                                if (dependency.VersionId is not null
                                    && !string.Equals(
                                        selectedDependency.Version.VersionId,
                                        dependency.VersionId,
                                        StringComparison.Ordinal))
                                {
                                    throw new InvalidDataException(
                                        $"Dependency conflict: '{project.ProjectId}' requires '{dependencyProjectId}' version '{dependency.VersionId}', but '{selectedDependency.Version.VersionId}' is already selected.");
                                }
                                break;
                            }

                            var dependencyProject = await _provider.GetProjectAsync(
                                dependencyProjectId,
                                cancellationToken)
                                ?? throw new InvalidDataException(
                                    $"Required dependency project '{dependencyProjectId}' could not be resolved.");

                            await VisitAsync(dependencyProject, dependencyVersion, isRoot: false);
                            break;
                        }
                        case ModProviderDependencyType.Optional:
                            optional.Add(dependency);
                            break;
                        case ModProviderDependencyType.Incompatible:
                            incompatibleEdges.Add(new ModProviderIncompatibility(
                                project.ProjectId,
                                project.Title,
                                dependency));
                            break;
                        case ModProviderDependencyType.Embedded:
                            break;
                        default:
                            throw new InvalidDataException("Unknown provider dependency type.");
                    }
                }

                var entry = new ModDependencyPlanEntry(project, candidateVersion, isRoot);
                selected.Add(project.ProjectId, entry);
                ordered.Add(entry);
            }
            finally
            {
                visiting.Remove(project.ProjectId);
            }
        }
    }

    private void ValidateNodeIdentity(ModProviderProject project, ModProviderVersion version)
    {
        if (!string.Equals(project.ProviderId, _provider.ProviderId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(version.ProviderId, _provider.ProviderId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(project.ProjectId, version.ProjectId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Dependency node identity mismatch for project '{project.ProjectId}' and version '{version.VersionId}'.");
        }
    }

    private static bool MatchesIncompatibility(ModProviderDependency dependency, ModProviderVersion version)
    {
        var projectMatches = dependency.ProjectId is null
            || string.Equals(dependency.ProjectId, version.ProjectId, StringComparison.Ordinal);
        var versionMatches = dependency.VersionId is null
            || string.Equals(dependency.VersionId, version.VersionId, StringComparison.Ordinal);
        return projectMatches
               && versionMatches
               && (dependency.ProjectId is not null || dependency.VersionId is not null);
    }

    private static string RequireValue(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A value is required.", parameterName);
        return value.Trim();
    }
}
