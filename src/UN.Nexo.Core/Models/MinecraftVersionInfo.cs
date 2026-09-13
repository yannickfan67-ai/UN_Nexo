namespace UN.Nexo.Core.Models;

public sealed record MinecraftVersionInfo(
    string Id,
    string Type,
    string Url,
    DateTimeOffset ReleaseTime,
    DateTimeOffset UpdatedAt,
    string Sha1,
    int ComplianceLevel)
{
    public string DisplayLabel => Type.Equals("release", StringComparison.OrdinalIgnoreCase)
        ? Id
        : $"{Id} · {Type}";
}
