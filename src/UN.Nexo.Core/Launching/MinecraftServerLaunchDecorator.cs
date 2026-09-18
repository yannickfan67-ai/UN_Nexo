using System.Globalization;
using System.Text.Json;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Launching;

public sealed class MinecraftServerLaunchDecorator(NexoPathService paths)
{
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
            arguments.Add("--quickPlayMultiplayer");
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
                if (item.GetString()?.Contains("quickPlayMultiplayer", StringComparison.Ordinal) == true)
                    return true;
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
                throw InvalidMetadata("arguments.game[]", "a string or object");
            if (!item.TryGetProperty("rules", out var rules))
                continue;
            if (rules.ValueKind != JsonValueKind.Array)
                throw InvalidMetadata("arguments.game[].rules", "an array");

            foreach (var rule in rules.EnumerateArray())
            {
                if (rule.ValueKind != JsonValueKind.Object)
                    throw InvalidMetadata("arguments.game[].rules[]", "an object");
                if (!rule.TryGetProperty("features", out var features))
                    continue;
                if (features.ValueKind != JsonValueKind.Object)
                    throw InvalidMetadata("arguments.game[].rules[].features", "an object");
                if (!features.TryGetProperty("is_quick_play_multiplayer", out var enabled))
                    continue;
                if (enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw InvalidMetadata(
                        "arguments.game[].rules[].features.is_quick_play_multiplayer",
                        "a boolean");
                if (enabled.ValueKind == JsonValueKind.True)
                    return true;
            }
        }

        return false;
    }

    private static InvalidDataException InvalidMetadata(string propertyName, string expected)
        => new($"Minecraft version metadata property '{propertyName}' must be {expected}.");
}
