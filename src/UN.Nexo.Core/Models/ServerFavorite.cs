namespace UN.Nexo.Core.Models;

public sealed record ServerFavorite(
    string Id,
    string Name,
    string Address,
    DateTimeOffset CreatedAt,
    string? DefaultInstanceId = null);
