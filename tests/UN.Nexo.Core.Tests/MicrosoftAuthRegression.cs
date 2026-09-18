using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class MicrosoftAuthRegression
{
    private const string MicrosoftToken = "TEST-MSA-ACCESS-TOKEN";
    private const string XboxToken = "TEST-XBOX-USER-TOKEN";
    private const string XstsToken = "TEST-XSTS-TOKEN";
    private const string MinecraftToken = "TEST-MINECRAFT-ACCESS-TOKEN";
    private const string HomeAccountId = "home-account.test";
    private const string ProfileId = "1234567890abcdef1234567890abcdef";

    internal static async Task RunAsync()
    {
        await TestHappyPathAndNoPlaintextTokenPersistenceAsync();
        await TestInvalidAppRegistrationAsync();
        await TestMissingEntitlementAsync();
    }

    private static async Task TestHappyPathAndNoPlaintextTokenPersistenceAsync()
    {
        var root = NewRoot();
        try
        {
            var accounts = new AccountStoreService(new NexoPathService(root));
            var provider = new FakeMicrosoftTokenProvider();
            var handler = new AuthHandler();
            using var client = new HttpClient(handler);
            var service = new MicrosoftMinecraftAuthService(client, accounts, provider);

            var session = await service.SignInAsync();

            Assert(session.Account.IsMicrosoft, "Signed-in account should be marked Microsoft.");
            Assert(session.Account.DisplayName == "NexoTester", "Minecraft profile name was not persisted.");
            Assert(session.Account.AuthenticationId == HomeAccountId,
                "Only the non-secret MSAL account identity should be persisted.");
            Assert(session.Credentials.AccessToken == MinecraftToken,
                "Minecraft access token should reach the in-memory launch credentials.");
            Assert(session.Credentials.ClientId == MsalMicrosoftAccessTokenProvider.ClientId,
                "Launch credentials must use UN_Nexo's own Microsoft Client ID.");
            Assert(session.Credentials.Xuid == "2814639012345678",
                "XUID should be carried from XSTS into launch credentials.");

            Assert(handler.SawXboxUserExchange, "Microsoft token was not exchanged with Xbox user auth.");
            Assert(handler.SawXstsExchange, "Xbox user token was not exchanged with XSTS.");
            Assert(handler.SawMinecraftLogin, "XSTS token was not exchanged with Minecraft Services.");
            Assert(handler.SawEntitlement, "Minecraft entitlement was not checked.");
            Assert(handler.SawProfile, "Minecraft Java profile was not queried.");

            var accountsPath = Path.Combine(root, "accounts.json");
            var persisted = await File.ReadAllTextAsync(accountsPath);
            foreach (var secret in new[] { MicrosoftToken, XboxToken, XstsToken, MinecraftToken })
                Assert(!persisted.Contains(secret, StringComparison.Ordinal),
                    "accounts.json must never contain Microsoft/Xbox/Minecraft access tokens.");

            var refreshed = await service.AcquireSessionAsync(session.Account);
            Assert(provider.SilentCalls == 1, "Online launch should acquire the Microsoft token silently.");
            Assert(refreshed.Account.Id == session.Account.Id,
                "Silent refresh should keep the same Minecraft account identity.");
            Assert(refreshed.Credentials.AccessToken == MinecraftToken,
                "Silent refresh should produce fresh in-memory Minecraft launch credentials.");

            await service.SignOutAsync(refreshed.Account);
            Assert(provider.SignOutCalls == 1, "Sign-out should remove the MSAL account from its secure cache.");
            Assert((await accounts.GetAllAsync()).Count == 0,
                "Sign-out should remove Microsoft profile metadata from accounts.json.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task TestInvalidAppRegistrationAsync()
    {
        var root = NewRoot();
        try
        {
            var accounts = new AccountStoreService(new NexoPathService(root));
            var provider = new FakeMicrosoftTokenProvider();
            var handler = new AuthHandler { InvalidAppRegistration = true };
            using var client = new HttpClient(handler);
            var service = new MicrosoftMinecraftAuthService(client, accounts, provider);

            try
            {
                await service.SignInAsync();
                throw new Exception("Invalid Minecraft app registration should block account creation.");
            }
            catch (MinecraftApplicationNotAuthorizedException ex)
            {
                Assert(ex.Message.Contains("Client ID", StringComparison.OrdinalIgnoreCase)
                       || ex.Message.Contains("application registration", StringComparison.OrdinalIgnoreCase),
                    "App-registration failure should be actionable.");
                Assert(!ex.Message.Contains(MicrosoftToken, StringComparison.Ordinal)
                       && !ex.Message.Contains(XstsToken, StringComparison.Ordinal),
                    "Authentication errors must not echo tokens.");
            }

            Assert((await accounts.GetAllAsync()).Count == 0,
                "Rejected Minecraft app registration must not publish an account profile.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task TestMissingEntitlementAsync()
    {
        var root = NewRoot();
        try
        {
            var accounts = new AccountStoreService(new NexoPathService(root));
            var provider = new FakeMicrosoftTokenProvider();
            var handler = new AuthHandler { MissingEntitlement = true };
            using var client = new HttpClient(handler);
            var service = new MicrosoftMinecraftAuthService(client, accounts, provider);

            try
            {
                await service.SignInAsync();
                throw new Exception("Account without Minecraft entitlement should be rejected.");
            }
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("entitlement", StringComparison.OrdinalIgnoreCase))
            {
            }

            Assert((await accounts.GetAllAsync()).Count == 0,
                "Account without Minecraft Java entitlement must not be persisted as launchable.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-microsoft-auth-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class FakeMicrosoftTokenProvider : IMicrosoftAccessTokenProvider
    {
        public int SilentCalls { get; private set; }
        public int SignOutCalls { get; private set; }

        public Task<MicrosoftAccessToken> AcquireInteractiveAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult(new MicrosoftAccessToken(
                MicrosoftToken,
                HomeAccountId,
                DateTimeOffset.UtcNow.AddHours(1)));

        public Task<MicrosoftAccessToken> AcquireSilentAsync(
            string homeAccountId,
            CancellationToken cancellationToken = default)
        {
            if (homeAccountId != HomeAccountId)
                throw new MicrosoftAuthenticationRequiredException("Unknown test account.");
            SilentCalls++;
            return Task.FromResult(new MicrosoftAccessToken(
                MicrosoftToken,
                HomeAccountId,
                DateTimeOffset.UtcNow.AddHours(1)));
        }

        public Task SignOutAsync(
            string homeAccountId,
            CancellationToken cancellationToken = default)
        {
            if (homeAccountId == HomeAccountId)
                SignOutCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class AuthHandler : HttpMessageHandler
    {
        public bool InvalidAppRegistration { get; set; }
        public bool MissingEntitlement { get; set; }
        public bool SawXboxUserExchange { get; private set; }
        public bool SawXstsExchange { get; private set; }
        public bool SawMinecraftLogin { get; private set; }
        public bool SawEntitlement { get; private set; }
        public bool SawProfile { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Missing auth request URI.");

            if (uri.Host == "user.auth.xboxlive.com")
            {
                using var body = await ReadJsonAsync(request, cancellationToken);
                var properties = body.RootElement.GetProperty("Properties");
                Assert(properties.GetProperty("RpsTicket").GetString() == "d=" + MicrosoftToken,
                    "Xbox user auth must receive the Microsoft RPS ticket.");
                Assert(body.RootElement.GetProperty("RelyingParty").GetString() == "http://auth.xboxlive.com",
                    "Xbox user auth relying party changed.");
                SawXboxUserExchange = true;
                return Json(
                    $$"""
                    {
                      "Token": "{{XboxToken}}",
                      "DisplayClaims": {
                        "xui": [
                          { "uhs": "1234567890", "xid": "2814639012345678" }
                        ]
                      }
                    }
                    """);
            }

            if (uri.Host == "xsts.auth.xboxlive.com")
            {
                using var body = await ReadJsonAsync(request, cancellationToken);
                var root = body.RootElement;
                Assert(root.GetProperty("RelyingParty").GetString() == "rp://api.minecraftservices.com/",
                    "XSTS relying party must target Minecraft Services.");
                Assert(root.GetProperty("Properties").GetProperty("UserTokens")[0].GetString() == XboxToken,
                    "XSTS must receive the Xbox user token.");
                SawXstsExchange = true;
                return Json(
                    $$"""
                    {
                      "Token": "{{XstsToken}}",
                      "DisplayClaims": {
                        "xui": [
                          { "uhs": "1234567890", "xid": "2814639012345678" }
                        ]
                      }
                    }
                    """);
            }

            if (uri.AbsolutePath.EndsWith("/authentication/login_with_xbox", StringComparison.Ordinal))
            {
                using var body = await ReadJsonAsync(request, cancellationToken);
                Assert(
                    body.RootElement.GetProperty("identityToken").GetString()
                    == "XBL3.0 x=1234567890;" + XstsToken,
                    "Minecraft login identity token is malformed.");
                SawMinecraftLogin = true;

                if (InvalidAppRegistration)
                    return Json(
                        """{"errorType":"ForbiddenOperationException","error":"Invalid app registration"}""",
                        HttpStatusCode.Forbidden);

                return Json(
                    $$"""{"access_token":"{{MinecraftToken}}","token_type":"Bearer","expires_in":3600}""");
            }

            if (uri.AbsolutePath.EndsWith("/entitlements/mcstore", StringComparison.Ordinal))
            {
                RequireBearer(request.Headers.Authorization, MinecraftToken);
                SawEntitlement = true;
                return MissingEntitlement
                    ? Json("""{"items":[]}""")
                    : Json("""{"items":[{"name":"game_minecraft","signature":"test"}]}""");
            }

            if (uri.AbsolutePath.EndsWith("/minecraft/profile", StringComparison.Ordinal))
            {
                RequireBearer(request.Headers.Authorization, MinecraftToken);
                SawProfile = true;
                return Json(
                    $$"""{"id":"{{ProfileId}}","name":"NexoTester","skins":[],"capes":[]}""");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static async Task<JsonDocument> ReadJsonAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var text = await (request.Content?.ReadAsStringAsync(cancellationToken)
                              ?? Task.FromResult("{}"));
            return JsonDocument.Parse(text);
        }

        private static void RequireBearer(AuthenticationHeaderValue? header, string expectedToken)
        {
            Assert(header?.Scheme == "Bearer" && header.Parameter == expectedToken,
                "Minecraft Services request is missing the expected bearer token.");
        }

        private static HttpResponseMessage Json(
            string body,
            HttpStatusCode statusCode = HttpStatusCode.OK)
            => new(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
    }
}
