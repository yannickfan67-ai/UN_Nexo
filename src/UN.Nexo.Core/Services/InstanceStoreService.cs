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
            try
            {
                // An instance directory is a storage boundary, not an arbitrary filesystem link.
                // Reject symlinks/junctions/reparse points before opening metadata so discovery
                // cannot turn an external directory into a managed instance.
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    continue;
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

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
            catch (UnauthorizedAccessException) { }
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
        var normalizedName = RequireValue(name, nameof(name), "Instance name");
        var normalizedVersionId = RequireValue(versionId, nameof(versionId), "Minecraft version");
        var normalizedLoader = RequireValue(loader, nameof(loader), "Loader");

        _paths.EnsureDirectories();
        var instancesRoot = _paths.GetInstancesRoot();
        var nameGatePath = Path.Combine(instancesRoot, ".instance-name-gate");
        using var nameLease = await PathKeyedLock.AcquireAsync(
            nameGatePath,
            cancellationToken);

        var existing = await GetAllAsync(cancellationToken);
        if (existing.Any(instance =>
                instance.Name.Equals(
                    normalizedName,
                    StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                $"An instance named '{normalizedName}' already exists.");

        var id = Guid.NewGuid().ToString("N");
        var instance = new GameInstance(
            id,
            normalizedName,
            normalizedVersionId,
            normalizedLoader,
            DateTimeOffset.UtcNow,
            string.IsNullOrWhiteSpace(baseVersionId) ? null : baseVersionId.Trim(),
            string.IsNullOrWhiteSpace(loaderVersion) ? null : loaderVersion.Trim());
        var directory = Path.Combine(instancesRoot, id);
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

    private static string RequireValue(string value, string parameterName, string displayName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{displayName} is required.", parameterName);

        return value.Trim();
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
