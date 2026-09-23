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
        => ReadAccountsAsync(cancellationToken);

    public async Task<LauncherAccount> CreateOfflineAsync(string username, CancellationToken cancellationToken = default)
    {
        var normalized = username.Trim();
        if (!OfflineNameRegex().IsMatch(normalized))
            throw new ArgumentException("Offline name must be 3-16 characters using letters, numbers or underscore.", nameof(username));

        await using var lease = await PersistedStoreMutationLock.AcquireAsync(GetAccountsPath(), cancellationToken);
        var accounts = (await ReadAccountsAsync(cancellationToken)).ToList();
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
        await using var lease = await PersistedStoreMutationLock.AcquireAsync(GetAccountsPath(), cancellationToken);
        var accounts = (await ReadAccountsAsync(cancellationToken)).ToList();

        var existingIndex = accounts.FindIndex(account =>
            account is not null && string.Equals(account.Id, accountId, StringComparison.Ordinal));
        var verifiedAt = DateTimeOffset.UtcNow;
        var createdAt = existingIndex >= 0 ? accounts[existingIndex].CreatedAt : verifiedAt;
        var account = new LauncherAccount(
            accountId,
            "microsoft",
            normalizedName,
            normalizedUuid,
            createdAt)
        {
            AuthenticationId = authenticationId,
            EntitlementVerifiedAt = verifiedAt
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

        await using var lease = await PersistedStoreMutationLock.AcquireAsync(GetAccountsPath(), cancellationToken);
        var accounts = (await ReadAccountsAsync(cancellationToken)).ToList();
        var removed = accounts.RemoveAll(account =>
            account is not null && string.Equals(account.Id, accountId, StringComparison.Ordinal));
        if (removed > 0)
            await SaveAtomicAsync(accounts, cancellationToken);
    }

    private async Task<IReadOnlyList<LauncherAccount>> ReadAccountsAsync(
        CancellationToken cancellationToken)
    {
        _paths.EnsureDirectories();
        var path = GetAccountsPath();
        if (!File.Exists(path))
            return [];

        try
        {
            var accounts = await BoundedPersistedJson.DeserializeAsync<List<LauncherAccount?>>(
                               path,
                               _jsonOptions,
                               cancellationToken)
                           ?? throw new InvalidDataException(
                               "Existing account store contains a null root. The original accounts.json was preserved.");
            if (accounts.Count > BoundedPersistedJson.MaxStoreEntries)
                throw new InvalidDataException(
                    $"Account store exceeds the {BoundedPersistedJson.MaxStoreEntries}-entry safety limit. The original accounts.json was preserved.");
            return ValidateAccounts(accounts);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                "Existing account store is malformed. The original accounts.json was preserved.",
                ex);
        }
    }

    private static IReadOnlyList<LauncherAccount> ValidateAccounts(
        IReadOnlyList<LauncherAccount?> accounts)
    {
        var result = new List<LauncherAccount>(accounts.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < accounts.Count; index++)
        {
            var account = accounts[index]
                ?? throw new InvalidDataException(
                    $"Account store entry {index} is null.");

            if (string.IsNullOrWhiteSpace(account.Id)
                || account.Id.Length > 512
                || account.Id.Any(char.IsControl))
                throw new InvalidDataException(
                    $"Account store entry {index} has an invalid id.");
            if (!ids.Add(account.Id))
                throw new InvalidDataException(
                    $"Account store contains duplicate id '{account.Id}'.");

            if (!OfflineNameRegex().IsMatch(account.DisplayName ?? string.Empty))
                throw new InvalidDataException(
                    $"Account store entry '{account.Id}' has an invalid Minecraft profile name.");
            if (!Guid.TryParse(account.Uuid, out var parsedUuid))
                throw new InvalidDataException(
                    $"Account store entry '{account.Id}' has an invalid UUID.");

            var normalizedUuid = parsedUuid.ToString("D");
            var type = account.Type?.Trim();
            if (string.Equals(type, "offline", StringComparison.OrdinalIgnoreCase))
            {
                if (!account.Id.StartsWith("offline:", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"Offline account '{account.Id}' has an invalid id kind.");
                account = account with
                {
                    Type = "offline",
                    Uuid = normalizedUuid,
                    AuthenticationId = null,
                    EntitlementVerifiedAt = null
                };
            }
            else if (string.Equals(type, "microsoft", StringComparison.OrdinalIgnoreCase))
            {
                if (!account.Id.StartsWith("microsoft:", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(account.AuthenticationId)
                    || account.AuthenticationId.Length > 512)
                    throw new InvalidDataException(
                        $"Microsoft account '{account.Id}' has invalid authentication metadata.");
                account = account with { Type = "microsoft", Uuid = normalizedUuid };
            }
            else
            {
                throw new InvalidDataException(
                    $"Account store entry '{account.Id}' has unsupported type '{account.Type}'.");
            }

            result.Add(account);
        }

        return result;
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
