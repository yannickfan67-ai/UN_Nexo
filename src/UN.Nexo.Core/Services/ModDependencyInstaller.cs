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

        var staged = new List<(
            ModDependencyPlanEntry Entry,
            ModProviderStagedInstall Staged,
            ModProviderInstalledMatch? Existing)>();

        try
        {
            foreach (var entry in plan.InstallOrder)
            {
                cancellationToken.ThrowIfCancellationRequested();

                installedMatches.TryGetValue(
                    entry.Project.ProjectId,
                    out var existing);
                if (existing is not null && existing.IsCurrent(entry.Version))
                    continue;

                var stagedInstall = await _provider.StageAsync(
                    entry.Project,
                    entry.Version,
                    cancellationToken);

                if (!string.Equals(
                        stagedInstall.Project.ProjectId,
                        entry.Project.ProjectId,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        stagedInstall.Version.VersionId,
                        entry.Version.VersionId,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "The provider staged a different project/version than the dependency plan requested.");
                }

                staged.Add((entry, stagedInstall, existing));
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
        finally
        {
            foreach (var item in staged)
                TryDeleteDirectory(item.Staged.CleanupDirectory);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}
