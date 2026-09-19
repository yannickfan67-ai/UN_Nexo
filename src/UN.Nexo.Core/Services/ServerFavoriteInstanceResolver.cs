using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed record ServerFavoriteInstanceResolution(
    GameInstance? Instance,
    bool HasStaleDefault);

public static class ServerFavoriteInstanceResolver
{
    public static ServerFavoriteInstanceResolution Resolve(
        ServerFavorite? favorite,
        IEnumerable<GameInstance> instances,
        GameInstance? fallbackInstance)
    {
        ArgumentNullException.ThrowIfNull(instances);

        if (favorite is not { DefaultInstanceId: { Length: > 0 } defaultId })
            return new ServerFavoriteInstanceResolution(
                fallbackInstance,
                HasStaleDefault: false);

        var linked = instances.FirstOrDefault(instance =>
            string.Equals(
                instance.Id,
                defaultId,
                StringComparison.Ordinal));
        return linked is null
            ? new ServerFavoriteInstanceResolution(
                Instance: null,
                HasStaleDefault: true)
            : new ServerFavoriteInstanceResolution(
                linked,
                HasStaleDefault: false);
    }
}
