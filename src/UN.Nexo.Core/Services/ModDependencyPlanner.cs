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

        if (!string.Equals(rootProject.ProviderId, _provider.ProviderId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(rootVersion.ProviderId, _provider.ProviderId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(rootProject.ProjectId, rootVersion.ProjectId, StringComparison.Ordinal))
            throw new InvalidDataException("The dependency root does not belong to this provider.");

        var version = RequireValue(minecraftVersion, nameof(minecraftVersion));
        var normalizedLoader = RequireValue(loader, nameof(loader));
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var selected = new Dictionary<string, ModDependencyPlanEntry>(StringComparer.Ordinal);
        var ordered = new List<ModDependencyPlanEntry>();
        var optional = new List<ModProviderDependency>();
        var incompatibleEdges = new List<(string Source, ModProviderDependency Dependency)>();

        await VisitAsync(rootProject, rootVersion, isRoot: true);

        foreach (var edge in incompatibleEdges)
        {
            if (selected.ContainsKey(edge.Dependency.ProjectId))
            {
                throw new InvalidDataException(
                    $"Dependency conflict: '{edge.Source}' is incompatible with '{edge.Dependency.ProjectId}'.");
            }
        }

        return new ModDependencyPlan(
            ordered,
            optional
                .GroupBy(item => (item.ProjectId, item.VersionId, item.Type))
                .Select(group => group.First())
                .ToArray());

        async Task VisitAsync(
            ModProviderProject project,
            ModProviderVersion candidateVersion,
            bool isRoot)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (selected.TryGetValue(project.ProjectId, out var existing))
            {
                if (!string.Equals(
                        existing.Version.VersionId,
                        candidateVersion.VersionId,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Dependency conflict: project '{project.ProjectId}' requires multiple versions ('{existing.Version.VersionId}' and '{candidateVersion.VersionId}').");
                }
                return;
            }

            if (!visiting.Add(project.ProjectId))
                throw new InvalidDataException(
                    $"Dependency cycle detected at project '{project.ProjectId}'.");

            if (selected.Count + visiting.Count > MaxDependencyProjects)
                throw new InvalidDataException(
                    $"The dependency graph exceeds the {MaxDependencyProjects}-project safety limit.");

            try
            {
                foreach (var dependency in candidateVersion.Dependencies)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    switch (dependency.Type)
                    {
                        case ModProviderDependencyType.Required:
                        {
                            if (visiting.Contains(dependency.ProjectId))
                            {
                                throw new InvalidDataException(
                                    $"Dependency cycle detected: '{project.ProjectId}' -> '{dependency.ProjectId}'.");
                            }

                            if (selected.TryGetValue(dependency.ProjectId, out var selectedDependency))
                            {
                                if (dependency.VersionId is not null
                                    && !string.Equals(
                                        selectedDependency.Version.VersionId,
                                        dependency.VersionId,
                                        StringComparison.Ordinal))
                                {
                                    throw new InvalidDataException(
                                        $"Dependency conflict: '{project.ProjectId}' requires '{dependency.ProjectId}' version '{dependency.VersionId}', but '{selectedDependency.Version.VersionId}' is already selected.");
                                }
                                break;
                            }

                            var dependencyProject = await _provider.GetProjectAsync(
                                dependency.ProjectId,
                                cancellationToken)
                                ?? throw new InvalidDataException(
                                    $"Required dependency project '{dependency.ProjectId}' could not be resolved.");

                            var dependencyVersion = await _provider.GetCompatibleVersionAsync(
                                dependency.ProjectId,
                                dependency.VersionId,
                                version,
                                normalizedLoader,
                                cancellationToken)
                                ?? throw new InvalidDataException(
                                    $"Required dependency '{dependencyProject.Title}' has no compatible version for Minecraft {version} / {normalizedLoader}.");

                            await VisitAsync(
                                dependencyProject,
                                dependencyVersion,
                                isRoot: false);
                            break;
                        }
                        case ModProviderDependencyType.Optional:
                            optional.Add(dependency);
                            break;
                        case ModProviderDependencyType.Incompatible:
                            incompatibleEdges.Add((project.ProjectId, dependency));
                            break;
                        case ModProviderDependencyType.Embedded:
                            break;
                        default:
                            throw new InvalidDataException(
                                $"Unknown dependency type for project '{dependency.ProjectId}'.");
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

    private static string RequireValue(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A value is required.", parameterName);
        return value.Trim();
    }
}
