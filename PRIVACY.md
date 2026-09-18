# UN_Nexo Privacy Notice

**Last updated: 2026-09-18**

UN_Nexo is an independent open-source Minecraft: Java Edition launcher. It is not an official Mojang or Microsoft product.

## Data kept on your device

UN_Nexo stores launcher configuration, instance metadata, offline profiles, and Microsoft Minecraft profile metadata in its local application data directory.

For a Microsoft profile, the local `accounts.json` entry contains the Minecraft display name, Minecraft UUID, creation time, account type, and a non-secret MSAL home-account identifier. It does not contain Microsoft passwords, access tokens, refresh tokens, Xbox tokens, XSTS tokens, or Minecraft access tokens.

Microsoft refresh credentials are handled by Microsoft's MSAL library and stored through operating-system credential protection. On Linux, UN_Nexo requires the Secret Service/libsecret keyring and does not intentionally fall back to an unprotected plaintext token cache.

Short-lived Xbox, XSTS, and Minecraft access tokens are used in memory while signing in or launching the game.

## Network services

When you choose Microsoft sign-in, authentication data is sent directly to Microsoft identity, Xbox authentication/XSTS, and Minecraft Services endpoints required for the login and entitlement/profile checks.

Other launcher features can contact Mojang/Minecraft download services, configured download mirrors, Modrinth, and servers that you explicitly query or launch.

UN_Nexo does not require a separate UN_Nexo-operated account service for Microsoft authentication.

## Logs and diagnostic bundles

UN_Nexo avoids writing authentication response bodies, access tokens, refresh tokens, or complete launch command lines to its normal logs. Diagnostic sanitization redacts common bearer, assignment, command-line, and JSON token forms before a diagnostic bundle is written.

A diagnostic preview may still contain ordinary launcher, Minecraft, Java, platform, file-path, and crash information needed to diagnose a problem. Review diagnostic content before sharing it with another person or service.

## Removing account data

Using **Sign out selected** for a Microsoft account removes that account from UN_Nexo's MSAL token cache and removes its local profile metadata. Deleting UN_Nexo's application-data directory removes the launcher's remaining local configuration and instance metadata, but does not delete your Microsoft, Xbox, or Minecraft account.

## Third-party privacy terms

Microsoft, Mojang/Minecraft, Modrinth, mirrors, and multiplayer servers process data under their own terms and privacy policies. UN_Nexo does not control those third-party services.

## Changes

This notice may be updated as UN_Nexo gains services that materially change what data is stored or transmitted. Repository history provides the public change record.
