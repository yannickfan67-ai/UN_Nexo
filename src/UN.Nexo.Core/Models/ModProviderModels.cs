namespace UN.Nexo.Core.Models;

public sealed record ModProviderProject(
    string ProviderId,
    string ProjectId,
    string Slug,
    string Title,
    string Description,
    string Author,
    string? IconUrl,
    long Downloads,
    string ProjectUrl);

public sealed record ModProviderFile(
    string FileName,
    string DownloadUrl,
    string Sha1,
    long Size,
    bool Primary);

public enum ModProviderDependencyType
{
    Required,
    Optional,
    Incompatible,
    Embedded
}

public sealed record ModProviderDependency(
    string ProjectId,
    string? VersionId,
    ModProviderDependencyType Type);

public sealed record ModProviderVersion(
    string ProviderId,
    string ProjectId,
    string VersionId,
    string Name,
    string VersionNumber,
    DateTimeOffset PublishedAt,
    IReadOnlyList<ModProviderFile> Files)
{
    public IReadOnlyList<ModProviderDependency> Dependencies { get; init; } = [];

    public ModProviderFile SelectPrimaryFile()
        => Files.FirstOrDefault(file => file.Primary)
           ?? Files.FirstOrDefault()
           ?? throw new InvalidDataException("The provider version has no installable files.");
}

public sealed record ModProviderInstalledMatch(
    string ProviderId,
    string ProjectId,
    string VersionId,
    string VersionNumber,
    string LocalFileName,
    bool IsEnabled)
{
    public bool IsCurrent(ModProviderVersion version)
        => string.Equals(ProviderId, version.ProviderId, StringComparison.OrdinalIgnoreCase)
           && string.Equals(ProjectId, version.ProjectId, StringComparison.Ordinal)
           && string.Equals(VersionId, version.VersionId, StringComparison.Ordinal);
}

public sealed record ModProviderInstallResult(
    ModProviderProject Project,
    ModProviderVersion Version,
    InstalledMod InstalledMod);

public sealed record ModProviderStagedInstall(
    ModProviderProject Project,
    ModProviderVersion Version,
    string StagedPath,
    string CleanupDirectory);

public sealed record ModDependencyPlanEntry(
    ModProviderProject Project,
    ModProviderVersion Version,
    bool IsRoot);

public sealed record ModDependencyPlan(
    IReadOnlyList<ModDependencyPlanEntry> InstallOrder,
    IReadOnlyList<ModProviderDependency> OptionalDependencies)
{
    public int RequiredDependencyCount => Math.Max(0, InstallOrder.Count - 1);
}
