namespace UN.Nexo.Core.Models;

public sealed record GameInstance(
    string Id,
    string Name,
    string VersionId,
    string Loader,
    DateTimeOffset CreatedAt,
    string? BaseVersionId = null,
    string? LoaderVersion = null)
{
    public string MinecraftVersionId => string.IsNullOrWhiteSpace(BaseVersionId)
        ? VersionId
        : BaseVersionId;
}
