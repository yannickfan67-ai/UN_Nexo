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

    public async Task SaveAsync(LauncherRuntimeSettings settings, CancellationToken cancellationToken = default)
    {
        RuntimeLaunchOptions.Validate(settings);
        _paths.EnsureDirectories();
        var path = GetSettingsPath();
        var temporary = path + ".tmp";
        try
        {
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, settings, _jsonOptions, cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private string GetSettingsPath() => Path.Combine(_paths.GetDataRoot(), "runtime-settings.json");
}
