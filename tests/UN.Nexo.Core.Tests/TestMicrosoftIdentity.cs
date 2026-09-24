using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal sealed record TestMicrosoftIdentity(
    LauncherAccount Account,
    MinecraftLaunchCredentials Credentials)
{
    internal static TestMicrosoftIdentity Create(
        string displayName,
        Guid? uuid = null)
    {
        var value = uuid ?? Guid.NewGuid();
        var account = new LauncherAccount(
            "microsoft:" + value.ToString("N"),
            "microsoft",
            displayName,
            value.ToString("D"),
            DateTimeOffset.UtcNow)
        {
            AuthenticationId = "test-home-account:" + value.ToString("N"),
            EntitlementVerifiedAt = DateTimeOffset.UtcNow
        };
        var credentials = new MinecraftLaunchCredentials(
            account.Id,
            account.DisplayName,
            account.Uuid,
            "TEST-MINECRAFT-ACCESS-TOKEN",
            MsalMicrosoftAccessTokenProvider.ClientId,
            "2814639012345678");
        return new TestMicrosoftIdentity(account, credentials);
    }
}
