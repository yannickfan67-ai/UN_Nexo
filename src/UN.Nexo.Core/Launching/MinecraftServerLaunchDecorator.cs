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
        var metadataPath = Path.Combine(
            paths.GetInstanceGameDirectory(instance.Id),
            "versions",
            instance.VersionId,
            instance.VersionId + ".json");
        if (!File.Exists(metadataPath))
            throw new FileNotFoundException("Version metadata is missing. Prepare the instance again.", metadataPath);

        await using var stream = File.OpenRead(metadataPath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var quickPlay = SupportsQuickPlayMultiplayer(document.RootElement);

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
        if (!root.TryGetProperty("arguments", out var arguments)
            || !arguments.TryGetProperty("game", out var gameArguments))
            return false;

        foreach (var item in gameArguments.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                if (item.GetString()?.Contains("quickPlayMultiplayer", StringComparison.Ordinal) == true)
                    return true;
                continue;
            }

            if (!item.TryGetProperty("rules", out var rules))
                continue;
            foreach (var rule in rules.EnumerateArray())
            {
                if (rule.TryGetProperty("features", out var features)
                    && features.TryGetProperty("is_quick_play_multiplayer", out var enabled)
                    && enabled.ValueKind == JsonValueKind.True)
                    return true;
            }
        }

        return false;
    }
}
