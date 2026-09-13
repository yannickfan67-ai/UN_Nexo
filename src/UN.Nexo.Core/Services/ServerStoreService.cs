using System.Text.Json;
using UN.Nexo.Core.Launching;
using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed class ServerStoreService
{
    private readonly NexoPathService _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public ServerStoreService(NexoPathService paths)
    {
        _paths = paths;
    }

    public async Task<IReadOnlyList<ServerFavorite>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        var path = GetPath();
        if (!File.Exists(path))
            return [];

        try
        {
            await using var stream = File.OpenRead(path);
            var items = await JsonSerializer.DeserializeAsync<List<ServerFavorite>>(stream, _json, cancellationToken)
                ?? [];
            return items
                .Where(item => !string.IsNullOrWhiteSpace(item.Id))
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    public async Task<ServerFavorite> AddAsync(
        string name,
        string address,
        CancellationToken cancellationToken = default)
    {
        var target = MinecraftServerTarget.Parse(address);
        var normalizedName = string.IsNullOrWhiteSpace(name) ? target.Authority : name.Trim();
        if (normalizedName.Length > 80)
            throw new ArgumentException("Server name is too long.", nameof(name));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var items = (await GetAllUnlockedAsync(cancellationToken)).ToList();
            var existing = items.FirstOrDefault(item =>
                item.Address.Equals(target.Authority, StringComparison.OrdinalIgnoreCase));
            var favorite = existing is null
                ? new ServerFavorite(Guid.NewGuid().ToString("N"), normalizedName, target.Authority, DateTimeOffset.UtcNow)
                : existing with { Name = normalizedName, Address = target.Authority };

            if (existing is null)
                items.Add(favorite);
            else
                items[items.IndexOf(existing)] = favorite;

            await SaveUnlockedAsync(items, cancellationToken);
            return favorite;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ServerFavorite?> SetDefaultInstanceAsync(
        string id,
        string? instanceId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Server id is required.", nameof(id));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var items = (await GetAllUnlockedAsync(cancellationToken)).ToList();
            var index = items.FindIndex(item => item.Id.Equals(id, StringComparison.Ordinal));
            if (index < 0)
                return null;

            var normalizedInstanceId = string.IsNullOrWhiteSpace(instanceId) ? null : instanceId.Trim();
            var updated = items[index] with { DefaultInstanceId = normalizedInstanceId };
            items[index] = updated;
            await SaveUnlockedAsync(items, cancellationToken);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var items = (await GetAllUnlockedAsync(cancellationToken))
                .Where(item => !item.Id.Equals(id, StringComparison.Ordinal))
                .ToList();
            await SaveUnlockedAsync(items, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<ServerFavorite>> GetAllUnlockedAsync(CancellationToken cancellationToken)
    {
        _paths.EnsureDirectories();
        var path = GetPath();
        if (!File.Exists(path))
            return [];
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<List<ServerFavorite>>(stream, _json, cancellationToken)
                ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    private async Task SaveUnlockedAsync(IReadOnlyList<ServerFavorite> items, CancellationToken cancellationToken)
    {
        _paths.EnsureDirectories();
        var path = GetPath();
        var temp = path + ".tmp";
        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, items, _json, cancellationToken);
        File.Move(temp, path, overwrite: true);
    }

    private string GetPath() => Path.Combine(_paths.GetDataRoot(), "servers.json");
}
