using System.Text.Json;
using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed class MinecraftRuntimeInspector(NexoPathService paths)
{
    public async Task<int?> GetRequiredJavaMajorAsync(
        GameInstance instance,
        CancellationToken cancellationToken = default)
    {
        var metadataPath = Path.Combine(
            paths.GetInstanceGameDirectory(instance.Id),
            "versions", instance.VersionId, instance.VersionId + ".json");
        if (!File.Exists(metadataPath))
            return null;

        try
        {
            await using var stream = File.OpenRead(metadataPath);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            return root.TryGetProperty("javaVersion", out var javaVersion)
                && javaVersion.TryGetProperty("majorVersion", out var major)
                ? major.GetInt32()
                : 8;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
