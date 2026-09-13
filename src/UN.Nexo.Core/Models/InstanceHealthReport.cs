namespace UN.Nexo.Core.Models;

public enum InstanceHealthLevel
{
    Healthy,
    Warning,
    Error
}

public sealed record InstanceHealthIssue(
    string Code,
    string Component,
    InstanceHealthLevel Level,
    string Message,
    string? Path = null,
    string? SuggestedAction = null);

public sealed record InstanceHealthReport(
    string InstanceId,
    string VersionId,
    int? RequiredJavaMajor,
    IReadOnlyList<InstanceHealthIssue> Issues,
    DateTimeOffset CheckedAt)
{
    public int ErrorCount => Issues.Count(item => item.Level == InstanceHealthLevel.Error);
    public int WarningCount => Issues.Count(item => item.Level == InstanceHealthLevel.Warning);
    public bool IsHealthy => ErrorCount == 0;

    public string Summary => ErrorCount > 0
        ? $"{ErrorCount} problem{(ErrorCount == 1 ? string.Empty : "s")} · {WarningCount} warning{(WarningCount == 1 ? string.Empty : "s")}" 
        : WarningCount > 0
            ? $"No broken game files · {WarningCount} warning{(WarningCount == 1 ? string.Empty : "s")}" 
            : "Instance looks healthy";
}
