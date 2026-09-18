namespace UN.Nexo.Core.Models;

public sealed record MicrosoftAccessToken(
    string AccessToken,
    string HomeAccountId,
    DateTimeOffset ExpiresOn);

public sealed record MinecraftLaunchCredentials(
    string AccountId,
    string PlayerName,
    string Uuid,
    string AccessToken,
    string ClientId,
    string Xuid);

public sealed record MicrosoftMinecraftSession(
    LauncherAccount Account,
    MinecraftLaunchCredentials Credentials);

public sealed class MicrosoftAuthenticationRequiredException(string message) : InvalidOperationException(message);

public sealed class MinecraftApplicationNotAuthorizedException(string message) : InvalidOperationException(message);
