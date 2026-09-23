using System.Text.Json;
using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed class LauncherSettingsService
{
    private readonly NexoPathService _paths;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public LauncherSettingsService(NexoPathService paths)
    {
        _paths = paths;
    }

    public async Task<LauncherSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        var path = GetSettingsPath();
        if (!File.Exists(path))
            return new LauncherSettings();

        try
        {
            return await BoundedPersistedJson.DeserializeAsync<LauncherSettings>(
                       path,
                       _jsonOptions,
                       cancellationToken)
                   ?? throw new InvalidDataException(
                       "Existing launcher settings contain a null root. The original settings.json was preserved.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                "Existing launcher settings are malformed. The original settings.json was preserved.",
                ex);
        }
    }

    // SaveAsync accepts a complete snapshot. Publication is serialized across Nexo processes;
    // callers still own snapshot freshness, so a later complete snapshot intentionally wins.
    // SaveAsync accepts a complete snapshot. Publication is serialized across Nexo processes;
    // callers still own snapshot freshness, so a later complete snapshot intentionally wins.
    public async Task SaveAsync(
        LauncherSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _paths.EnsureDirectories();
        var path = GetSettingsPath();
        await using var writeLease =
            await PersistedStoreMutationLock.AcquireAsync(
                path,
                cancellationToken);
        await AtomicJsonFile.WriteUnlockedAsync(
            path,
            settings,
            _jsonOptions,
            cancellationToken);
    }

    private string GetSettingsPath() => Path.Combine(_paths.GetDataRoot(), "settings.json");
}
