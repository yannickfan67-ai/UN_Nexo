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


## Restricted-region offline fallback

UN_Nexo does not use IP geolocation to bypass Minecraft ownership checks. Normal play uses a Microsoft-authenticated Minecraft session.

For service-restricted regions, Nexo may offer a narrow continuity fallback only when all of the following are true:

- the public IP geolocation check positively identifies `CN` or `RU`;
- the selected profile is a Microsoft profile that previously completed the Minecraft entitlement and Java-profile checks on this device; and
- the current online session refresh failed because of a transient network/service failure rather than an explicit authentication, entitlement, identity, or AppID rejection.

The launcher stores only the non-secret time of the last successful entitlement/profile verification. It does not persist the public IP address or country result. The geolocation response is used in memory only. A geolocation timeout, malformed response, redirect, unknown country, or failed request does not grant offline access.

Generic offline profiles are not a substitute for this policy and are not eligible to launch under the restricted-region fallback.


## Explicit offline test mode

Packaged builds recognize the exact command-line switch `--test-offline` for launcher development and troubleshooting. The switch is never enabled by default.

When the launcher is started with `--test-offline`, a locally configured Offline profile may launch without a Microsoft/Minecraft session so maintainers can quickly isolate Java, instance, loader, mod, graphics, and process-launch failures from authentication failures. The window title and launch status are visibly marked `TEST OFFLINE`.

This mode does not represent the profile as Microsoft-authenticated, does not create entitlement metadata, does not grant a Minecraft Services token, and does not change the normal launch policy when the switch is absent. It is a diagnostic/test path and must not be presented as proof of Minecraft ownership.
