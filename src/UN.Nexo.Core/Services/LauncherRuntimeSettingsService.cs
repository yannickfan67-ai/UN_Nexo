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

        try
        {
            await using var stream = File.OpenRead(path);
            var settings = await JsonSerializer.DeserializeAsync<LauncherRuntimeSettings>(
                stream, _jsonOptions, cancellationToken) ?? new LauncherRuntimeSettings();
            RuntimeLaunchOptions.Validate(settings);
            return settings;
        }
        catch (JsonException)
        {
            return new LauncherRuntimeSettings();
        }
        catch (IOException)
        {
            return new LauncherRuntimeSettings();
        }
        catch (ArgumentException)
        {
            return new LauncherRuntimeSettings();
        }
    }

    // SaveAsync accepts a complete snapshot. Saves targeting the same canonical file are
    // serialized process-wide; if callers submit stale snapshots, the last writer wins.
    public async Task SaveAsync(LauncherRuntimeSettings settings, CancellationToken cancellationToken = default)
    {
        RuntimeLaunchOptions.Validate(settings);
        _paths.EnsureDirectories();
        var path = GetSettingsPath();
        var saveGate = SettingsSaveGate.ForPath(path);
        await saveGate.WaitAsync(cancellationToken);
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
            saveGate.Release();
        }
    }

    private string GetSettingsPath() => Path.Combine(_paths.GetDataRoot(), "runtime-settings.json");
}
