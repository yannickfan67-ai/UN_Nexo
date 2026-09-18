using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed class MicrosoftMinecraftAuthService
{
    private const int MaxResponseBytes = 1024 * 1024;
    private static readonly Uri XboxUserAuthenticateUri =
        new("https://user.auth.xboxlive.com/user/authenticate");
    private static readonly Uri XboxXstsAuthorizeUri =
        new("https://xsts.auth.xboxlive.com/xsts/authorize");
    private static readonly Uri MinecraftLoginUri =
        new("https://api.minecraftservices.com/authentication/login_with_xbox");
    private static readonly Uri MinecraftEntitlementsUri =
        new("https://api.minecraftservices.com/entitlements/mcstore");
    private static readonly Uri MinecraftProfileUri =
        new("https://api.minecraftservices.com/minecraft/profile");

    private readonly HttpClient _httpClient;
    private readonly AccountStoreService _accounts;
    private readonly IMicrosoftAccessTokenProvider _microsoftTokens;

    public MicrosoftMinecraftAuthService(
        HttpClient httpClient,
        AccountStoreService accounts,
        IMicrosoftAccessTokenProvider microsoftTokens)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _microsoftTokens = microsoftTokens ?? throw new ArgumentNullException(nameof(microsoftTokens));
    }

    public async Task<MicrosoftMinecraftSession> SignInAsync(
        CancellationToken cancellationToken = default)
    {
        var microsoft = await _microsoftTokens.AcquireInteractiveAsync(cancellationToken);
        var exchanged = await ExchangeAsync(microsoft.AccessToken, cancellationToken);
        var account = await _accounts.UpsertMicrosoftAsync(
            exchanged.PlayerName,
            exchanged.Uuid,
            microsoft.HomeAccountId,
            cancellationToken);

        return new MicrosoftMinecraftSession(
            account,
            exchanged with { AccountId = account.Id });
    }

    public async Task<MicrosoftMinecraftSession> AcquireSessionAsync(
        LauncherAccount account,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!account.IsMicrosoft)
            throw new InvalidOperationException("The selected profile is not a Microsoft account.");
        if (string.IsNullOrWhiteSpace(account.AuthenticationId))
            throw new MicrosoftAuthenticationRequiredException(
                "This Microsoft profile predates secure sign-in support. Sign in again from Accounts.");

        var microsoft = await _microsoftTokens.AcquireSilentAsync(
            account.AuthenticationId,
            cancellationToken);
        var exchanged = await ExchangeAsync(microsoft.AccessToken, cancellationToken);

        if (!Guid.TryParse(account.Uuid, out var existingUuid)
            || !Guid.TryParse(exchanged.Uuid, out var currentUuid)
            || existingUuid != currentUuid)
            throw new MicrosoftAuthenticationRequiredException(
                "The Minecraft profile identity no longer matches the saved Microsoft profile. Sign in again from Accounts.");

        var updated = await _accounts.UpsertMicrosoftAsync(
            exchanged.PlayerName,
            exchanged.Uuid,
            microsoft.HomeAccountId,
            cancellationToken);

        return new MicrosoftMinecraftSession(
            updated,
            exchanged with { AccountId = updated.Id });
    }

    public async Task SignOutAsync(
        LauncherAccount account,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!account.IsMicrosoft)
            return;

        if (!string.IsNullOrWhiteSpace(account.AuthenticationId))
            await _microsoftTokens.SignOutAsync(account.AuthenticationId, cancellationToken);
        await _accounts.RemoveAsync(account.Id, cancellationToken);
    }

    private async Task<MinecraftLaunchCredentials> ExchangeAsync(
        string microsoftAccessToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(microsoftAccessToken))
            throw new InvalidDataException("Microsoft returned an empty access token.");

        var xbox = await AcquireXboxUserTokenAsync(microsoftAccessToken, cancellationToken);
        var xsts = await AcquireXstsTokenAsync(xbox.Token, cancellationToken);
        var minecraftAccessToken = await AcquireMinecraftAccessTokenAsync(
            xsts.UserHash,
            xsts.Token,
            cancellationToken);

        await RequireMinecraftEntitlementAsync(minecraftAccessToken, cancellationToken);
        var profile = await GetMinecraftProfileAsync(minecraftAccessToken, cancellationToken);

        var xuid = !string.IsNullOrWhiteSpace(xsts.Xuid)
            ? xsts.Xuid
            : !string.IsNullOrWhiteSpace(xbox.Xuid)
                ? xbox.Xuid
                : "0";

        return new MinecraftLaunchCredentials(
            string.Empty,
            profile.PlayerName,
            profile.Uuid,
            minecraftAccessToken,
            MsalMicrosoftAccessTokenProvider.ClientId,
            xuid);
    }

    private async Task<XboxToken> AcquireXboxUserTokenAsync(
        string microsoftAccessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, XboxUserAuthenticateUri);
        request.Headers.TryAddWithoutValidation("x-xbl-contract-version", "1");
        request.Content = JsonContent(new
        {
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT",
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",
                RpsTicket = "d=" + microsoftAccessToken
            }
        });

        using var document = await SendJsonAsync(request, "Xbox user authentication", cancellationToken);
        return ReadXboxToken(document.RootElement, "Xbox user authentication");
    }

    private async Task<XboxToken> AcquireXstsTokenAsync(
        string xboxUserToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, XboxXstsAuthorizeUri);
        request.Headers.TryAddWithoutValidation("x-xbl-contract-version", "1");
        request.Content = JsonContent(new
        {
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT",
            Properties = new
            {
                SandboxId = "RETAIL",
                UserTokens = new[] { xboxUserToken }
            }
        });

        using var document = await SendJsonAsync(request, "Xbox XSTS authorization", cancellationToken);
        return ReadXboxToken(document.RootElement, "Xbox XSTS authorization");
    }

    private async Task<string> AcquireMinecraftAccessTokenAsync(
        string userHash,
        string xstsToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, MinecraftLoginUri)
        {
            Content = JsonContent(new
            {
                identityToken = $"XBL3.0 x={userHash};{xstsToken}"
            })
        };

        using var document = await SendJsonAsync(
            request,
            "Minecraft Services sign-in",
            cancellationToken,
            detectMinecraftRegistrationFailure: true);

        return RequireString(document.RootElement, "access_token", "Minecraft Services sign-in");
    }

    private async Task RequireMinecraftEntitlementAsync(
        string minecraftAccessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, MinecraftEntitlementsUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", minecraftAccessToken);

        using var document = await SendJsonAsync(request, "Minecraft entitlement check", cancellationToken);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Minecraft entitlement response is missing its items array.");
        if (items.GetArrayLength() == 0)
            throw new InvalidOperationException(
                "This Microsoft account does not currently report a Minecraft Java entitlement.");
    }

    private async Task<MinecraftProfile> GetMinecraftProfileAsync(
        string minecraftAccessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, MinecraftProfileUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", minecraftAccessToken);

        using var document = await SendJsonAsync(request, "Minecraft profile lookup", cancellationToken);
        var id = RequireString(document.RootElement, "id", "Minecraft profile lookup");
        var name = RequireString(document.RootElement, "name", "Minecraft profile lookup");

        if (!Guid.TryParseExact(id, "N", out var uuid) && !Guid.TryParse(id, out uuid))
            throw new InvalidDataException("Minecraft returned an invalid Java profile UUID.");
        if (!Regex.IsMatch(name, "^[A-Za-z0-9_]{3,16}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Minecraft returned an invalid Java profile name.");

        return new MinecraftProfile(name, uuid.ToString("D"));
    }

    private async Task<JsonDocument> SendJsonAsync(
        HttpRequestMessage request,
        string serviceName,
        CancellationToken cancellationToken,
        bool detectMinecraftRegistrationFailure = false)
    {
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (response.RequestMessage?.RequestUri is { } effectiveUri
            && request.RequestUri is { } requestedUri
            && !Uri.Equals(effectiveUri, requestedUri))
            throw new InvalidDataException(
                $"{serviceName} unexpectedly redirected to another endpoint.");

        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            throw new InvalidDataException($"{serviceName} returned an oversized response.");

        var bytes = await ReadBoundedAsync(response.Content, serviceName, cancellationToken);
        var body = Encoding.UTF8.GetString(bytes);

        if (!response.IsSuccessStatusCode)
        {
            if (detectMinecraftRegistrationFailure
                && response.StatusCode == HttpStatusCode.Forbidden
                && body.Contains("Invalid app registration", StringComparison.OrdinalIgnoreCase))
                throw new MinecraftApplicationNotAuthorizedException(
                    "Microsoft and Xbox sign-in succeeded, but Minecraft Services rejected UN_Nexo's application registration. The UN_Nexo Client ID must be authorized by Minecraft Services; Nexo will not borrow another launcher's Client ID.");

            if (TryReadXboxError(body, out var xboxMessage))
                throw new InvalidOperationException(xboxMessage);

            throw new HttpRequestException(
                $"{serviceName} returned HTTP {(int)response.StatusCode} ({response.StatusCode}).",
                null,
                response.StatusCode);
        }

        try
        {
            return JsonDocument.Parse(bytes);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{serviceName} returned malformed JSON.", ex);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        string serviceName,
        CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        await using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];

        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;
            if (output.Length + read > MaxResponseBytes)
                throw new InvalidDataException($"{serviceName} response exceeded the size limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return output.ToArray();
    }

    private static XboxToken ReadXboxToken(JsonElement root, string serviceName)
    {
        var token = RequireString(root, "Token", serviceName);
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("DisplayClaims", out var claims)
            || claims.ValueKind != JsonValueKind.Object
            || !claims.TryGetProperty("xui", out var xui)
            || xui.ValueKind != JsonValueKind.Array
            || xui.GetArrayLength() == 0
            || xui[0].ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{serviceName} returned incomplete Xbox display claims.");

        var userHash = RequireString(xui[0], "uhs", serviceName);
        var xuid = TryString(xui[0], "xid") ?? TryString(xui[0], "xuid");
        return new XboxToken(token, userHash, xuid);
    }

    private static bool TryReadXboxError(string body, out string message)
    {
        message = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("XErr", out var error)
                || !error.TryGetInt64(out var code))
                return false;

            message = code switch
            {
                2148916233 => "This Microsoft account does not have an Xbox profile. Create/sign in to an Xbox profile, then try again.",
                2148916235 => "Xbox sign-in is unavailable for this account's region.",
                2148916238 => "Xbox requires adult/family approval for this account before Minecraft sign-in can continue.",
                _ => $"Xbox authorization failed with XErr {code}."
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string RequireString(JsonElement element, string name, string serviceName)
        => TryString(element, name)
           ?? throw new InvalidDataException($"{serviceName} response is missing '{name}'.");

    private static string? TryString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    private static StringContent JsonContent<T>(T value)
        => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private sealed record XboxToken(string Token, string UserHash, string? Xuid);
    private sealed record MinecraftProfile(string PlayerName, string Uuid);
}
