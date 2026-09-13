using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed partial class AccountStoreService
{
    private readonly NexoPathService _paths;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public AccountStoreService(NexoPathService paths)
    {
        _paths = paths;
    }

    public async Task<IReadOnlyList<LauncherAccount>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        var path = GetAccountsPath();
        if (!File.Exists(path))
            return [];

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<List<LauncherAccount>>(stream, _jsonOptions, cancellationToken) ?? [];
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

    public async Task<LauncherAccount> CreateOfflineAsync(string username, CancellationToken cancellationToken = default)
    {
        var normalized = username.Trim();
        if (!OfflineNameRegex().IsMatch(normalized))
            throw new ArgumentException("Offline name must be 3-16 characters using letters, numbers or underscore.", nameof(username));

        var accounts = (await GetAllAsync(cancellationToken)).ToList();
        var existing = accounts.FirstOrDefault(x => x.IsOffline && x.DisplayName.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            return existing;

        var account = new LauncherAccount(
            $"offline:{normalized.ToLowerInvariant()}",
            "offline",
            normalized,
            CreateOfflineUuid(normalized),
            DateTimeOffset.UtcNow);

        accounts.Add(account);
        await SaveAsync(accounts, cancellationToken);
        return account;
    }

    private async Task SaveAsync(IReadOnlyList<LauncherAccount> accounts, CancellationToken cancellationToken)
    {
        _paths.EnsureDirectories();
        await using var stream = File.Create(GetAccountsPath());
        await JsonSerializer.SerializeAsync(stream, accounts, _jsonOptions, cancellationToken);
    }

    private string GetAccountsPath() => Path.Combine(_paths.GetDataRoot(), "accounts.json");

    private static string CreateOfflineUuid(string username)
    {
        var digest = MD5.HashData(Encoding.UTF8.GetBytes($"OfflinePlayer:{username}"));
        digest[6] = (byte)((digest[6] & 0x0F) | 0x30);
        digest[8] = (byte)((digest[8] & 0x3F) | 0x80);
        var hex = Convert.ToHexString(digest).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..32]}";
    }

    [GeneratedRegex("^[A-Za-z0-9_]{3,16}$", RegexOptions.CultureInvariant)]
    private static partial Regex OfflineNameRegex();
}
