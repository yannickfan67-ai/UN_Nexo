using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Fabric.Tests;

internal sealed record TestMicrosoftIdentity(
    LauncherAccount Account,
    MinecraftLaunchCredentials Credentials)
{
    internal static TestMicrosoftIdentity Create(string displayName)
    {
        var uuid = Guid.NewGuid();
        var account = new LauncherAccount(
            "microsoft:" + uuid.ToString("N"),
            "microsoft",
            displayName,
            uuid.ToString("D"),
            DateTimeOffset.UtcNow)
        {
            AuthenticationId = "test-home-account:" + uuid.ToString("N"),
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
