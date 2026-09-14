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
        catch (InvalidDataException)
        {
            return null;
        }
    }
}
