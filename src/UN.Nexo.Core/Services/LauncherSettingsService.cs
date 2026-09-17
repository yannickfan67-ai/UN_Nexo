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

    // SaveAsync accepts a complete snapshot. Saves targeting the same canonical file are
    // serialized process-wide; if callers submit stale snapshots, the last writer wins.
    public async Task SaveAsync(LauncherSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _paths.EnsureDirectories();
        var path = GetSettingsPath();
        var writeGate = SettingsSaveGate.ForPath(path);
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(
                                 temporary,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 16 * 1024,
                                 FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, settings, _jsonOptions, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary))
                        File.Delete(temporary);
                }
                catch
                {
                    // Best-effort temporary-file cleanup only.
                }
            }
        }
        finally
        {
            writeGate.Release();
        }
    }

    private string GetSettingsPath() => Path.Combine(_paths.GetDataRoot(), "settings.json");
}
