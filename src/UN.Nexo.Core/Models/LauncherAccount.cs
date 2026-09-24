namespace UN.Nexo.Core.Models;

public sealed record LauncherAccount(
    string Id,
    string Type,
    string DisplayName,
    string Uuid,
    DateTimeOffset CreatedAt)
{
    public string? AuthenticationId { get; init; }
    public DateTimeOffset? EntitlementVerifiedAt { get; init; }

    public bool IsMicrosoft => Type.Equals("microsoft", StringComparison.OrdinalIgnoreCase);
    public string TypeLabel => IsMicrosoft ? "Microsoft" : Type;
    public string Subtitle => $"{TypeLabel} · {Uuid}";
}
