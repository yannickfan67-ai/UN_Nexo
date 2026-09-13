namespace UN.Nexo.Core.Models;

public sealed record LauncherAccount(
    string Id,
    string Type,
    string DisplayName,
    string Uuid,
    DateTimeOffset CreatedAt)
{
    public bool IsOffline => Type.Equals("offline", StringComparison.OrdinalIgnoreCase);
    public string TypeLabel => IsOffline ? "Offline" : "Microsoft";
    public string Subtitle => $"{TypeLabel} · {Uuid}";
}
