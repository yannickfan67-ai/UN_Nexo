namespace UN.Nexo.Core.Models;

public sealed record LauncherAccount(
    string Id,
    string Type,
    string DisplayName,
    string Uuid,
    DateTimeOffset CreatedAt)
{
    public string? AuthenticationId { get; init; }
    public DateTimeOffset? LastEntitlementVerifiedAt { get; init; }
    public DateTimeOffset? EntitlementVerifiedAt { get; init; }

    public bool IsOffline => Type.Equals("offline", StringComparison.OrdinalIgnoreCase);
    public bool IsMicrosoft => Type.Equals("microsoft", StringComparison.OrdinalIgnoreCase);
    public string TypeLabel => IsOffline ? "Offline" : IsMicrosoft ? "Microsoft" : Type;
    public string Subtitle => $"{TypeLabel} · {Uuid}";
}
