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
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<LauncherSettings>(stream, _jsonOptions, cancellationToken)
                ?? new LauncherSettings();
        }
        catch (JsonException)
        {
            return new LauncherSettings();
        }
        catch (IOException)
        {
            return new LauncherSettings();
        }
    }

    public async Task SaveAsync(LauncherSettings settings, CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        await using var stream = File.Create(GetSettingsPath());
        await JsonSerializer.SerializeAsync(stream, settings, _jsonOptions, cancellationToken);
    }

    private string GetSettingsPath() => Path.Combine(_paths.GetDataRoot(), "settings.json");
}
