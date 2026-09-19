# Microsoft account authentication

UN_Nexo is a public desktop client. It uses its own Microsoft application registration and does not embed a client secret or reuse another launcher's identity.

## Registration

- Application (client) ID: `ac6485d3-1fd4-42d1-89c0-40ffee68d915`
- Authority: `https://login.microsoftonline.com/consumers`
- Redirect URI: `http://localhost`
- OAuth scopes: `xboxlive.signin`, `xboxlive.offline_access`
- Client type: public client
- Browser: system browser through MSAL.NET

The localhost callback uses the authorization-code flow with PKCE managed by MSAL.NET. A client secret must not be added to the distributed launcher.

## Sign-in chain

1. MSAL.NET authenticates the personal Microsoft account.
2. The Microsoft access token is exchanged for an Xbox user token.
3. The Xbox user token is exchanged for XSTS with the Minecraft Services relying party.
4. The XSTS identity is exchanged at Minecraft Services for a Minecraft access token.
5. Nexo checks the Minecraft entitlement response.
6. Nexo loads the Minecraft Java profile and records only its name, UUID, and the non-secret MSAL home-account identifier.
7. Immediately before an authenticated launch, Nexo performs the chain again from a silent MSAL refresh and passes the resulting Minecraft token to the launch plan in memory.

If silent acquisition requires interaction, Nexo asks the user to sign in again instead of attempting to retain or invent credentials.

## Token storage

`accounts.json` does **not** contain Microsoft, Xbox, XSTS, Minecraft access tokens, or refresh tokens.

MSAL persistence uses `Microsoft.Identity.Client.Extensions.Msal`:

- Windows: OS-protected MSAL storage.
- Linux: Secret Service/libsecret keyring.
- macOS: Keychain.

Nexo verifies that secure persistence is working before registering the cache. It deliberately does not fall back to the MSAL unprotected/plaintext cache mode. Linux packages therefore depend on `libsecret`.

Signing out removes the selected MSAL account from the persistent cache and removes the corresponding local account metadata.

## Diagnostics

Nexo does not log the authentication request/response bodies or complete launch command line. Diagnostic sanitization additionally redacts common bearer, command-line, assignment, and JSON credential forms including access tokens, refresh tokens, Xbox/XSTS token fields, RPS tickets, and Minecraft identity tokens.

## Minecraft Services application authorization

Microsoft/Xbox authentication can succeed while Minecraft Services still returns HTTP 403 with `Invalid app registration` for an application that has not been authorized on the Minecraft side.

UN_Nexo treats that as a separate application-authorization error. New or rejected application IDs can be submitted for Minecraft AppID review through Microsoft's official short link: [https://aka.ms/mce-reviewappid](https://aka.ms/mce-reviewappid). The launcher does not work around approval by borrowing a Client ID from the official launcher, Prism Launcher, or any other project. Once Minecraft Services authorizes the UN_Nexo registration, the same flow can continue without changing user account storage.
