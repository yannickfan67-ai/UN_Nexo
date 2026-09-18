using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public interface IMicrosoftAccessTokenProvider
{
    Task<MicrosoftAccessToken> AcquireInteractiveAsync(CancellationToken cancellationToken = default);
    Task<MicrosoftAccessToken> AcquireSilentAsync(string homeAccountId, CancellationToken cancellationToken = default);
    Task SignOutAsync(string homeAccountId, CancellationToken cancellationToken = default);
}

public sealed class MsalMicrosoftAccessTokenProvider : IMicrosoftAccessTokenProvider
{
    public const string ClientId = "ac6485d3-1fd4-42d1-89c0-40ffee68d915";
    public const string RedirectUri = "http://localhost";
    private static readonly string[] Scopes = ["xboxlive.signin", "xboxlive.offline_access"];

    private readonly NexoPathService _paths;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private IPublicClientApplication? _application;
    private MsalCacheHelper? _cacheHelper;

    public MsalMicrosoftAccessTokenProvider(NexoPathService paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public async Task<MicrosoftAccessToken> AcquireInteractiveAsync(
        CancellationToken cancellationToken = default)
    {
        var application = await GetApplicationAsync(cancellationToken);
        var result = await application
            .AcquireTokenInteractive(Scopes)
            .WithUseEmbeddedWebView(false)
            .WithPrompt(Prompt.SelectAccount)
            .ExecuteAsync(cancellationToken);

        return FromResult(result);
    }

    public async Task<MicrosoftAccessToken> AcquireSilentAsync(
        string homeAccountId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(homeAccountId))
            throw new MicrosoftAuthenticationRequiredException(
                "This Microsoft profile has no saved sign-in identity. Sign in again from Accounts.");

        var application = await GetApplicationAsync(cancellationToken);
        var account = (await application.GetAccountsAsync())
            .FirstOrDefault(item => string.Equals(
                item.HomeAccountId?.Identifier,
                homeAccountId,
                StringComparison.Ordinal));

        if (account is null)
            throw new MicrosoftAuthenticationRequiredException(
                "The Microsoft sign-in cache no longer contains this profile. Sign in again from Accounts.");

        try
        {
            var result = await application
                .AcquireTokenSilent(Scopes, account)
                .ExecuteAsync(cancellationToken);
            return FromResult(result);
        }
        catch (MsalUiRequiredException ex)
        {
            throw new MicrosoftAuthenticationRequiredException(
                "Microsoft needs you to sign in again before launching Minecraft.", ex);
        }
    }

    public async Task SignOutAsync(
        string homeAccountId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(homeAccountId))
            return;

        var application = await GetApplicationAsync(cancellationToken);
        var account = (await application.GetAccountsAsync())
            .FirstOrDefault(item => string.Equals(
                item.HomeAccountId?.Identifier,
                homeAccountId,
                StringComparison.Ordinal));
        if (account is not null)
            await application.RemoveAsync(account);
    }

    private async Task<IPublicClientApplication> GetApplicationAsync(CancellationToken cancellationToken)
    {
        if (_application is not null)
            return _application;

        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (_application is not null)
                return _application;

            var cacheDirectory = Path.Combine(_paths.GetDataRoot(), "auth");
            Directory.CreateDirectory(cacheDirectory);

            var storageBuilder = new StorageCreationPropertiesBuilder(
                "msal.cache",
                cacheDirectory);

            if (OperatingSystem.IsLinux())
            {
                storageBuilder = storageBuilder.WithLinuxKeyring(
                    schemaName: "com.yannickfan67_ai.UN_Nexo",
                    collection: "default",
                    secretLabel: "UN_Nexo Microsoft account token cache",
                    attribute1: new KeyValuePair<string, string>("Version", "1"),
                    attribute2: new KeyValuePair<string, string>("Product", "UN_Nexo"));
            }
            else if (OperatingSystem.IsMacOS())
            {
                storageBuilder = storageBuilder.WithMacKeyChain(
                    serviceName: "com.yannickfan67-ai.UN_Nexo",
                    accountName: "MicrosoftIdentityCache");
            }

            var application = PublicClientApplicationBuilder
                .Create(ClientId)
                .WithAuthority("https://login.microsoftonline.com/consumers")
                .WithRedirectUri(RedirectUri)
                .Build();

            var cacheHelper = await MsalCacheHelper.CreateAsync(storageBuilder.Build());
            cacheHelper.RegisterCache(application.UserTokenCache);

            _cacheHelper = cacheHelper;
            _application = application;
            return application;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private static MicrosoftAccessToken FromResult(AuthenticationResult result)
    {
        var homeAccountId = result.Account?.HomeAccountId?.Identifier;
        if (string.IsNullOrWhiteSpace(result.AccessToken) || string.IsNullOrWhiteSpace(homeAccountId))
            throw new InvalidDataException("Microsoft sign-in returned an incomplete authentication result.");

        return new MicrosoftAccessToken(result.AccessToken, homeAccountId, result.ExpiresOn);
    }
}
