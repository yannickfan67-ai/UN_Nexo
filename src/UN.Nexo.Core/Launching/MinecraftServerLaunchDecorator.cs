using System.Globalization;
using System.Text.Json;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Launching;

public sealed class MinecraftServerLaunchDecorator(NexoPathService paths)
{
    private const string QuickPlayMultiplayerOption = "--quickPlayMultiplayer";
    private static readonly IReadOnlyDictionary<string, bool> QuickPlayMultiplayerFeatures =
        new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["is_quick_play_multiplayer"] = true
        };
    public async Task<MinecraftLaunchPlan> ApplyAsync(
        MinecraftLaunchPlan plan,
        GameInstance instance,
        MinecraftServerTarget target,
        CancellationToken cancellationToken = default)
    {
        var gameRoot = paths.GetInstanceGameDirectory(instance.Id);
        using var resolved = await new MinecraftVersionMetadataResolver()
            .ResolveAsync(gameRoot, instance.VersionId, cancellationToken);
        var quickPlay = SupportsQuickPlayMultiplayer(resolved.Document.RootElement);

        var arguments = plan.Arguments.ToList();
        if (quickPlay)
        {
            arguments.Add(QuickPlayMultiplayerOption);
            arguments.Add(target.Authority);
        }
        else
        {
            arguments.Add("--server");
            arguments.Add(target.Host);
            arguments.Add("--port");
            arguments.Add(target.Port.ToString(CultureInfo.InvariantCulture));
        }

        return plan with { Arguments = arguments };
    }

    internal static bool SupportsQuickPlayMultiplayer(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw InvalidMetadata("root", "an object");
        if (!root.TryGetProperty("arguments", out var arguments))
            return false;
        if (arguments.ValueKind != JsonValueKind.Object)
            throw InvalidMetadata("arguments", "an object");
        if (!arguments.TryGetProperty("game", out var gameArguments))
            return false;
        if (gameArguments.ValueKind != JsonValueKind.Array)
            throw InvalidMetadata("arguments.game", "an array");

        foreach (var item in gameArguments.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                if (IsQuickPlayArgument(item.GetString()))
                    return true;
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
                throw InvalidMetadata("arguments.game[]", "a string or object");
            if (!MinecraftRules.Allows(item, QuickPlayMultiplayerFeatures))
                continue;
            if (!item.TryGetProperty("value", out var value))
                throw InvalidMetadata(
                    "arguments.game[].value",
                    "a string or array of strings");

            if (value.ValueKind == JsonValueKind.String)
            {
                if (IsQuickPlayArgument(value.GetString()))
                    return true;
                continue;
            }

            if (value.ValueKind != JsonValueKind.Array)
                throw InvalidMetadata(
                    "arguments.game[].value",
                    "a string or array of strings");

            foreach (var part in value.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.String)
                    throw InvalidMetadata(
                        "arguments.game[].value[]",
                        "a string");
                if (IsQuickPlayArgument(part.GetString()))
                    return true;
            }
        }

        return false;
    }

    private static bool IsQuickPlayArgument(string? value)
        => string.Equals(
            value,
            QuickPlayMultiplayerOption,
            StringComparison.Ordinal);

    private static InvalidDataException InvalidMetadata(string propertyName, string expected)
        => new($"Minecraft version metadata property '{propertyName}' must be {expected}.");
}
