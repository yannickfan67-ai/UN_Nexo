using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class ServerFavoriteInstanceRegression
{
    internal static Task RunAsync()
    {
        var instanceA = new GameInstance(
            "instance-a",
            "Instance A",
            "1.21.4",
            "vanilla",
            DateTimeOffset.UtcNow);
        var instanceB = new GameInstance(
            "instance-b",
            "Instance B",
            "1.20.1",
            "vanilla",
            DateTimeOffset.UtcNow);
        var instances = new[] { instanceA, instanceB };
        var baseFavorite = new ServerFavorite(
            "server-1",
            "Example",
            "example.com:25565",
            DateTimeOffset.UtcNow);

        var linked = ServerFavoriteInstanceResolver.Resolve(
            baseFavorite with { DefaultInstanceId = instanceA.Id },
            instances,
            instanceB);
        Assert(!linked.HasStaleDefault && linked.Instance?.Id == instanceA.Id,
            "Existing saved default must win over the current fallback instance.");

        var noDefault = ServerFavoriteInstanceResolver.Resolve(
            baseFavorite with { DefaultInstanceId = null },
            instances,
            instanceB);
        Assert(!noDefault.HasStaleDefault && noDefault.Instance?.Id == instanceB.Id,
            "Favorite without a saved default may use the explicit fallback instance.");

        var stale = ServerFavoriteInstanceResolver.Resolve(
            baseFavorite with { DefaultInstanceId = "deleted-instance" },
            instances,
            instanceB);
        Assert(stale.HasStaleDefault,
            "Missing saved instance id must be reported as stale.");
        Assert(stale.Instance is null,
            "Stale saved default must never silently substitute the current instance.");

        var cleared = ServerFavoriteInstanceResolver.Resolve(
            baseFavorite with { DefaultInstanceId = null },
            instances,
            instanceB);
        Assert(!cleared.HasStaleDefault && cleared.Instance == instanceB,
            "Clearing a stale default should restore normal fallback behavior.");

        var replaced = ServerFavoriteInstanceResolver.Resolve(
            baseFavorite with { DefaultInstanceId = instanceB.Id },
            instances,
            instanceA);
        Assert(!replaced.HasStaleDefault && replaced.Instance == instanceB,
            "Saving a replacement link should resolve to the newly linked instance.");

        return Task.CompletedTask;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
