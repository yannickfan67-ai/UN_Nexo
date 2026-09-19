using System.Text.Json;
using UN.Nexo.Core.Launching;
using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed class LauncherRuntimeSettingsService
{
    private readonly NexoPathService _paths;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public LauncherRuntimeSettingsService(NexoPathService paths)
    {
        _paths = paths;
    }

    public async Task<LauncherRuntimeSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        var path = GetSettingsPath();
        if (!File.Exists(path))
            return new LauncherRuntimeSettings();

        LauncherRuntimeSettings settings;
        try
        {
            await using var stream = File.OpenRead(path);
            settings = await JsonSerializer.DeserializeAsync<LauncherRuntimeSettings>(
                           stream,
                           _jsonOptions,
                           cancellationToken)
                       ?? throw new InvalidDataException(
                           "Existing runtime settings contain a null root. The original runtime-settings.json was preserved.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                "Existing runtime settings are malformed. The original runtime-settings.json was preserved.",
                ex);
        }

        try
        {
            RuntimeLaunchOptions.Validate(settings);
            return settings;
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException(
                "Existing runtime settings contain invalid launch options. The original runtime-settings.json was preserved.",
                ex);
        }
    }

    // SaveAsync accepts a complete snapshot. Publication is serialized across Nexo processes;
    // callers still own snapshot freshness, so a later complete snapshot intentionally wins.
    public async Task SaveAsync(LauncherRuntimeSettings settings, CancellationToken cancellationToken = default)
    {
        RuntimeLaunchOptions.Validate(settings);
        _paths.EnsureDirectories();
        var path = GetSettingsPath();
        await using var saveLease = await PersistedStoreMutationLock.AcquireAsync(path, cancellationToken);
        string? temporary = null;
        try
        {
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(stream, settings, _jsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            temporary = null;
        }
        finally
        {
            if (temporary is not null)
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }
    }

    private string GetSettingsPath() => Path.Combine(_paths.GetDataRoot(), "runtime-settings.json");
}
