using System.Text.Json;
using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed class InstanceStoreService
{
    internal const long MaxInstanceMetadataBytes = 64 * 1024;
    private readonly NexoPathService _paths;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public InstanceStoreService(NexoPathService paths)
    {
        _paths = paths;
    }

    public async Task<IReadOnlyList<GameInstance>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var instancesRoot = _paths.EnsureInstancesRootPhysical();
        var result = new List<GameInstance>();
        var idComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        foreach (var directory in Directory.EnumerateDirectories(instancesRoot))
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
                PhysicalPathGuard.EnsureRegularFileOrMissing(
                    metadataPath,
                    "Instance metadata");
                var bytes = await BoundedLocalFile.ReadAllBytesAsync(
                    metadataPath,
                    MaxInstanceMetadataBytes,
                    "Instance metadata",
                    cancellationToken);
                var instance = JsonSerializer.Deserialize<GameInstance>(
                    bytes,
                    _jsonOptions);
                if (instance is not null
                    && string.Equals(
                        instance.Id,
                        directoryId,
                        idComparison))
                {
                    result.Add(instance);
                }
            }
            catch (JsonException) { }
            catch (InvalidDataException) { }
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

        var instancesRoot = _paths.EnsureInstancesRootPhysical();
        var nameGatePath = Path.Combine(instancesRoot, ".instance-name-gate");
        await using var nameLease = await PersistedStoreMutationLock.AcquireAsync(
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
        _paths.EnsureInstancesRootPhysical();
        Directory.CreateDirectory(directory);
        _paths.EnsureInstanceDirectoryPhysical(id);

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
            _paths.EnsureInstanceDirectoryPhysical(id);
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
