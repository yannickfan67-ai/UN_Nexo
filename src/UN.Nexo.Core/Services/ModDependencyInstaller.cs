using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed class ModDependencyInstaller
{
    private readonly IModDependencyProvider _provider;
    private readonly InstanceModService _modService;

    public ModDependencyInstaller(
        IModDependencyProvider provider,
        InstanceModService modService)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _modService = modService ?? throw new ArgumentNullException(nameof(modService));
    }

    public async Task<ModDependencyInstallResult> InstallAsync(
        string instanceId,
        ModDependencyPlan plan,
        IReadOnlyDictionary<string, ModProviderInstalledMatch> installedMatches,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            throw new ArgumentException("An instance id is required.", nameof(instanceId));
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(installedMatches);

        ValidateInstalledIncompatibilities(
            _provider.ProviderId,
            plan,
            installedMatches);

        using var stagingScope = ProviderStagingScope.Create();
        var staged = new List<(
            ModDependencyPlanEntry Entry,
            ModProviderStagedInstall Staged,
            ModProviderInstalledMatch? Existing)>();

        foreach (var entry in plan.InstallOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();

            installedMatches.TryGetValue(
                entry.Project.ProjectId,
                out var existing);
            if (existing is not null && existing.IsCurrent(entry.Version))
                continue;

            var entryDirectory = stagingScope.CreateEntryDirectory();
            var stagedInstall = await _provider.StageAsync(
                entry.Project,
                entry.Version,
                entryDirectory,
                cancellationToken);

            ValidateStagedIdentity(_provider.ProviderId, entry, stagedInstall);

            var validatedPath = stagingScope.ValidateStagedFile(
                entryDirectory,
                stagedInstall.StagedPath);
            staged.Add((
                entry,
                stagedInstall with { StagedPath = validatedPath },
                existing));
        }

        if (staged.Count == 0)
            return new ModDependencyInstallResult(plan, []);

        var mutations = staged
            .Select(item => new ProviderModBatchItem(
                item.Staged.StagedPath,
                item.Existing?.LocalFileName,
                item.Existing?.IsEnabled ?? true))
            .ToArray();

        var installed = await _modService.InstallProviderBatchAsync(
            instanceId,
            mutations,
            cancellationToken);
        if (installed.Count != staged.Count)
            throw new InvalidDataException(
                "The provider dependency batch did not return one installed file per staged mutation.");

        var results = new ModProviderInstallResult[installed.Count];
        for (var index = 0; index < installed.Count; index++)
        {
            results[index] = new ModProviderInstallResult(
                staged[index].Entry.Project,
                staged[index].Entry.Version,
                installed[index]);
        }

        return new ModDependencyInstallResult(plan, results);
    }

    internal static void ValidateStagedIdentity(
        string providerId,
        ModDependencyPlanEntry entry,
        ModProviderStagedInstall stagedInstall)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            throw new ArgumentException("A provider id is required.", nameof(providerId));
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(stagedInstall);

        if (!string.Equals(
                entry.Project.ProviderId,
                providerId,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                entry.Version.ProviderId,
                providerId,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                entry.Version.ProjectId,
                entry.Project.ProjectId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The dependency plan contains inconsistent project/version/provider identity.");
        }

        if (!string.Equals(
                stagedInstall.Project.ProviderId,
                providerId,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                stagedInstall.Version.ProviderId,
                providerId,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                stagedInstall.Project.ProjectId,
                entry.Project.ProjectId,
                StringComparison.Ordinal)
            || !string.Equals(
                stagedInstall.Version.ProjectId,
                entry.Project.ProjectId,
                StringComparison.Ordinal)
            || !string.Equals(
                stagedInstall.Version.VersionId,
                entry.Version.VersionId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The provider staged a different project/version/provider identity than the dependency plan requested.");
        }
    }

    internal static void ValidateInstalledIncompatibilities(
        string providerId,
        ModDependencyPlan plan,
        IReadOnlyDictionary<string, ModProviderInstalledMatch> installedMatches)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            throw new ArgumentException("A provider id is required.", nameof(providerId));
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(installedMatches);

        var finalVersions = plan.InstallOrder.ToDictionary(
            entry => entry.Project.ProjectId,
            entry => entry.Version,
            StringComparer.Ordinal);

        foreach (var incompatibility in plan.Incompatibilities)
        {
            foreach (var installed in installedMatches.Values)
            {
                if (!string.Equals(
                        installed.ProviderId,
                        providerId,
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                string finalProjectId;
                string finalVersionId;
                string finalVersionNumber;

                if (finalVersions.TryGetValue(
                        installed.ProjectId,
                        out var replacement))
                {
                    finalProjectId = replacement.ProjectId;
                    finalVersionId = replacement.VersionId;
                    finalVersionNumber = replacement.VersionNumber;
                }
                else
                {
                    finalProjectId = installed.ProjectId;
                    finalVersionId = installed.VersionId;
                    finalVersionNumber = installed.VersionNumber;
                }

                if (!Matches(
                        incompatibility.Dependency,
                        finalProjectId,
                        finalVersionId))
                    continue;

                throw new InvalidDataException(
                    $"Dependency conflict: '{incompatibility.SourceTitle}' ({incompatibility.SourceProjectId}) is incompatible with installed project '{installed.ProjectId}' version '{installed.VersionNumber}' ({installed.VersionId}); the final mod set would keep project '{finalProjectId}' version '{finalVersionNumber}' ({finalVersionId}). Remove or update the conflicting mod before installing.");
            }
        }
    }

    private static bool Matches(
        ModProviderDependency dependency,
        string projectId,
        string versionId)
    {
        var projectMatches = dependency.ProjectId is null
            || string.Equals(
                dependency.ProjectId,
                projectId,
                StringComparison.Ordinal);
        var versionMatches = dependency.VersionId is null
            || string.Equals(
                dependency.VersionId,
                versionId,
                StringComparison.Ordinal);

        return projectMatches
               && versionMatches
               && (dependency.ProjectId is not null
                   || dependency.VersionId is not null);
    }
}
