namespace UN.Nexo.Core.Models;

public sealed record GameInstance(
    string Id,
    string Name,
    string VersionId,
    string Loader,
    DateTimeOffset CreatedAt);
