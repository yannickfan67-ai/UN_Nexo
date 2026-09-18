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

    public Task<IReadOnlyList<LauncherAccount>> GetAllAsync(CancellationToken cancellationToken = default)
        => ReadAccountsAsync(tolerateReadErrors: true, cancellationToken);

    public async Task<LauncherAccount> CreateOfflineAsync(string username, CancellationToken cancellationToken = default)
    {
        var normalized = username.Trim();
        if (!OfflineNameRegex().IsMatch(normalized))
            throw new ArgumentException("Offline name must be 3-16 characters using letters, numbers or underscore.", nameof(username));

        using var lease = await PathKeyedLock.AcquireAsync(GetAccountsPath(), cancellationToken);
        var accounts = (await ReadAccountsAsync(tolerateReadErrors: false, cancellationToken)).ToList();
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
        await SaveAtomicAsync(accounts, cancellationToken);
        return account;
    }

    public async Task<LauncherAccount> UpsertMicrosoftAsync(
        string displayName,
        string minecraftUuid,
        string authenticationId,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = displayName?.Trim() ?? string.Empty;
        if (!OfflineNameRegex().IsMatch(normalizedName))
            throw new InvalidDataException("Minecraft returned an invalid profile name.");
        if (!Guid.TryParse(minecraftUuid, out var parsedUuid))
            throw new InvalidDataException("Minecraft returned an invalid profile UUID.");
        if (string.IsNullOrWhiteSpace(authenticationId) || authenticationId.Length > 512)
            throw new InvalidDataException("Microsoft returned an invalid account cache identity.");

        var normalizedUuid = parsedUuid.ToString("D");
        var accountId = "microsoft:" + parsedUuid.ToString("N");
        using var lease = await PathKeyedLock.AcquireAsync(GetAccountsPath(), cancellationToken);
        var accounts = (await ReadAccountsAsync(tolerateReadErrors: false, cancellationToken)).ToList();

        var existingIndex = accounts.FindIndex(account =>
            account is not null && string.Equals(account.Id, accountId, StringComparison.Ordinal));
        var createdAt = existingIndex >= 0 ? accounts[existingIndex].CreatedAt : DateTimeOffset.UtcNow;
        var account = new LauncherAccount(
            accountId,
            "microsoft",
            normalizedName,
            normalizedUuid,
            createdAt)
        {
            AuthenticationId = authenticationId
        };

        if (existingIndex >= 0)
            accounts[existingIndex] = account;
        else
            accounts.Add(account);

        await SaveAtomicAsync(accounts, cancellationToken);
        return account;
    }

    public async Task RemoveAsync(string accountId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            return;

        using var lease = await PathKeyedLock.AcquireAsync(GetAccountsPath(), cancellationToken);
        var accounts = (await ReadAccountsAsync(tolerateReadErrors: false, cancellationToken)).ToList();
        var removed = accounts.RemoveAll(account =>
            account is not null && string.Equals(account.Id, accountId, StringComparison.Ordinal));
        if (removed > 0)
            await SaveAtomicAsync(accounts, cancellationToken);
    }

    private async Task<IReadOnlyList<LauncherAccount>> ReadAccountsAsync(
        bool tolerateReadErrors,
        CancellationToken cancellationToken)
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
        catch (JsonException) when (tolerateReadErrors)
        {
            return [];
        }
        catch (IOException) when (tolerateReadErrors)
        {
            return [];
        }
    }

    private async Task SaveAtomicAsync(IReadOnlyList<LauncherAccount> accounts, CancellationToken cancellationToken)
    {
        _paths.EnsureDirectories();
        var path = GetAccountsPath();
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temp,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(stream, accounts, _jsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDeleteFile(temp);
            throw;
        }
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

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_]{3,16}$", RegexOptions.CultureInvariant)]
    private static partial Regex OfflineNameRegex();
}
