using System.Text.Json;
using UN.Nexo.Core.Launching;
using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed class ServerStoreService
{
    private readonly NexoPathService _paths;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public ServerStoreService(NexoPathService paths) => _paths = paths;

    public async Task<IReadOnlyList<ServerFavorite>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var items = await ReadExistingAsync(cancellationToken);
        return items
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<ServerFavorite> AddAsync(string name, string address, CancellationToken cancellationToken = default)
    {
        var target = MinecraftServerTarget.Parse(address);
        var normalizedName = string.IsNullOrWhiteSpace(name) ? target.Authority : name.Trim();
        if (normalizedName.Length > 80) throw new ArgumentException("Server name is too long.", nameof(name));

        await using var lease = await PersistedStoreMutationLock.AcquireAsync(GetPath(), cancellationToken);
        var items = (await ReadExistingAsync(cancellationToken)).ToList();
        var existing = items.FirstOrDefault(item => item.Address.Equals(target.Authority, StringComparison.OrdinalIgnoreCase));
        var favorite = existing is null
            ? new ServerFavorite(Guid.NewGuid().ToString("N"), normalizedName, target.Authority, DateTimeOffset.UtcNow)
            : existing with { Name = normalizedName, Address = target.Authority };
        if (existing is null) items.Add(favorite); else items[items.IndexOf(existing)] = favorite;
        await SaveUnlockedAsync(items, cancellationToken);
        return favorite;
    }

    public async Task<ServerFavorite?> SetDefaultInstanceAsync(string id, string? instanceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Server id is required.", nameof(id));
        await using var lease = await PersistedStoreMutationLock.AcquireAsync(GetPath(), cancellationToken);
        var items = (await ReadExistingAsync(cancellationToken)).ToList();
        var index = items.FindIndex(item => item.Id.Equals(id, StringComparison.Ordinal));
        if (index < 0) return null;
        var normalizedInstanceId = string.IsNullOrWhiteSpace(instanceId) ? null : instanceId.Trim();
        var updated = items[index] with { DefaultInstanceId = normalizedInstanceId };
        items[index] = updated;
        await SaveUnlockedAsync(items, cancellationToken);
        return updated;
    }

    public async Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var lease = await PersistedStoreMutationLock.AcquireAsync(GetPath(), cancellationToken);
        var items = (await ReadExistingAsync(cancellationToken)).Where(item => !item.Id.Equals(id, StringComparison.Ordinal)).ToList();
        await SaveUnlockedAsync(items, cancellationToken);
    }

    private async Task<IReadOnlyList<ServerFavorite>> ReadExistingAsync(CancellationToken cancellationToken)
    {
        _paths.EnsureDirectories();
        var path = GetPath();
        if (!File.Exists(path)) return [];
        try
        {
            var items = await BoundedPersistedJson.DeserializeAsync<List<ServerFavorite?>>(
                            path,
                            _json,
                            cancellationToken)
                        ?? throw new InvalidDataException(
                            "Existing server favorite store contains a null root. The original servers.json was preserved.");
            return ValidateFavorites(items);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                "Existing server favorite store is malformed. The original servers.json was preserved.",
                ex);
        }
    }

    private static IReadOnlyList<ServerFavorite> ValidateFavorites(
        IReadOnlyList<ServerFavorite?> items)
    {
        if (items.Count > BoundedPersistedJson.MaxStoreEntries)
            throw new InvalidDataException(
                $"Server favorite store exceeds the {BoundedPersistedJson.MaxStoreEntries}-entry safety limit.");

        var result = new List<ServerFavorite>(items.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index]
                ?? throw new InvalidDataException(
                    $"Server favorite entry {index} is null.");

            if (string.IsNullOrWhiteSpace(item.Id)
                || item.Id.Length > 512
                || item.Id.Any(char.IsControl))
                throw new InvalidDataException(
                    $"Server favorite entry {index} has an invalid id.");
            if (!ids.Add(item.Id))
                throw new InvalidDataException(
                    $"Server favorite store contains duplicate id '{item.Id}'.");

            var name = item.Name?.Trim() ?? string.Empty;
            if (name.Length is < 1 or > 80 || name.Any(char.IsControl))
                throw new InvalidDataException(
                    $"Server favorite '{item.Id}' has an invalid name.");

            MinecraftServerTarget target;
            try
            {
                target = MinecraftServerTarget.Parse(item.Address);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException)
            {
                throw new InvalidDataException(
                    $"Server favorite '{item.Id}' has an invalid address.",
                    ex);
            }

            var defaultInstanceId = string.IsNullOrWhiteSpace(item.DefaultInstanceId)
                ? null
                : item.DefaultInstanceId.Trim();
            if (defaultInstanceId is { Length: > 512 }
                || defaultInstanceId?.Any(char.IsControl) == true)
                throw new InvalidDataException(
                    $"Server favorite '{item.Id}' has an invalid default instance id.");

            result.Add(item with
            {
                Name = name,
                Address = target.Authority,
                DefaultInstanceId = defaultInstanceId
            });
        }

        return result;
    }

    private async Task SaveUnlockedAsync(
        IReadOnlyList<ServerFavorite> items,
        CancellationToken cancellationToken)
    {
        _paths.EnsureDirectories();
        await AtomicJsonFile.WriteUnlockedAsync(
            GetPath(),
            items,
            _json,
            cancellationToken);
    }

    private string GetPath() => Path.Combine(_paths.GetDataRoot(), "servers.json");
    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
