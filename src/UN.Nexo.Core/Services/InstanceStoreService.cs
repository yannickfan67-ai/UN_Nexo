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
        var idComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        foreach (var directory in Directory.EnumerateDirectories(_paths.GetInstancesRoot()))
        {
            var directoryId = Path.GetFileName(directory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(directoryId))
                continue;

            var metadataPath = Path.Combine(directory, "instance.json");
            if (!File.Exists(metadataPath))
                continue;

            try
            {
                await using var stream = File.OpenRead(metadataPath);
                var instance = await JsonSerializer.DeserializeAsync<GameInstance>(stream, _jsonOptions, cancellationToken);
                if (instance is not null
                    && string.Equals(instance.Id, directoryId, idComparison))
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

        var metadataPath = Path.Combine(directory, "instance.json");
        var temporaryPath = metadataPath + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(stream, instance, _jsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, metadataPath);
            return instance;
        }
        catch
        {
            TryDeleteDirectory(directory);
            throw;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }
}
