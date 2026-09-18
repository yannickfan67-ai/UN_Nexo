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

public sealed class MicrosoftAuthenticationRequiredException : InvalidOperationException
{
    public MicrosoftAuthenticationRequiredException(string message) : base(message) { }
    public MicrosoftAuthenticationRequiredException(string message, Exception innerException)
        : base(message, innerException) { }
}

public sealed class MinecraftApplicationNotAuthorizedException : InvalidOperationException
{
    public MinecraftApplicationNotAuthorizedException(string message) : base(message) { }
}
