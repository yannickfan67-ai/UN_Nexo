using System.Text.Json;
using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed class MinecraftRuntimeInspector(NexoPathService paths)
{
    public async Task<int?> GetRequiredJavaMajorAsync(
        GameInstance instance,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var resolved = await new MinecraftVersionMetadataResolver()
                .ResolveAsync(paths.GetInstanceGameDirectory(instance.Id), instance.VersionId, cancellationToken);
            var root = resolved.Document.RootElement;
            if (!root.TryGetProperty("javaVersion", out var javaVersion))
                return 8;
            if (javaVersion.ValueKind != JsonValueKind.Object
                || !javaVersion.TryGetProperty("majorVersion", out var major)
                || major.ValueKind != JsonValueKind.Number
                || !major.TryGetInt32(out var value)
                || value <= 0)
                return null;
            return value;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}
