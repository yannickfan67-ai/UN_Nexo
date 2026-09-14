using System.Text.Json;
using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed class InstanceStoreService
{
    private readonly NexoPathService _paths;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public InstanceStoreService(NexoPathService paths)
    {
        _paths = paths;
    }

    public async Task<IReadOnlyList<GameInstance>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        var result = new List<GameInstance>();

        foreach (var directory in Directory.EnumerateDirectories(_paths.GetInstancesRoot()))
        {
            var metadataPath = Path.Combine(directory, "instance.json");
            if (!File.Exists(metadataPath))
                continue;

            try
            {
                await using var stream = File.OpenRead(metadataPath);
                var instance = await JsonSerializer.DeserializeAsync<GameInstance>(stream, _jsonOptions, cancellationToken);
                if (instance is not null)
                    result.Add(instance);
            }
            catch (JsonException) { }
            catch (IOException) { }
        }

        return result.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<GameInstance> CreateAsync(
        string name,
        string versionId,
        string loader = "vanilla",
        string? baseVersionId = null,
        string? loaderVersion = null,
        CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        var id = Guid.NewGuid().ToString("N");
        var instance = new GameInstance(
            id,
            name.Trim(),
            versionId.Trim(),
            loader.Trim(),
            DateTimeOffset.UtcNow,
            string.IsNullOrWhiteSpace(baseVersionId) ? null : baseVersionId.Trim(),
            string.IsNullOrWhiteSpace(loaderVersion) ? null : loaderVersion.Trim());
        var directory = Path.Combine(_paths.GetInstancesRoot(), id);
        Directory.CreateDirectory(directory);

        await using var stream = File.Create(Path.Combine(directory, "instance.json"));
        await JsonSerializer.SerializeAsync(stream, instance, _jsonOptions, cancellationToken);
        return instance;
    }
}
