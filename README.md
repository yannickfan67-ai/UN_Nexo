# UN_Nexo

UN_Nexo is an independent, cross-platform Minecraft: Java Edition launcher built with **C#**, **.NET 10** and **Avalonia 12**.

The launcher core is kept separate from the desktop UI so installation, accounts, launch logic and future mod-loader support can evolve without turning the UI project into a monolith.

> UN_Nexo is not an official Minecraft product and is not approved by or associated with Mojang or Microsoft.

## Current milestone — v0.4.1-dev

The current development line includes:

- Windows and Linux desktop UI with Home, Instances, Accounts and Settings pages
- Lightweight cold-start splash and main-window fade-in without heavy GPU effects
- Compact in-app game-session status overlay while Minecraft launches or runs
- Mojang version catalog with release history and recent snapshots
- Per-instance Vanilla preparation with client, libraries, natives and assets
- Official downloads or BMCLAPI acceleration with automatic official fallback and SHA-1 verification
- Persistent local/offline profiles, kept clearly separate from Microsoft authentication
- Java discovery through `JAVA_HOME`, `PATH` and common platform install locations
- Automatic exact-major Java selection from Minecraft version metadata
- Vanilla launch-plan generation for modern `arguments` metadata and legacy `minecraftArguments`
- Safe process launching through `ProcessStartInfo.ArgumentList`, including paths containing spaces
- Per-launch stdout/stderr logs, exit-code reporting and Stop game support
- Linux DEB/RPM and Windows self-contained EXE prerelease packages
- Linux package installation and packaged UI smoke checks in CI

Local/offline profiles are intended for local worlds, development and offline-mode test servers. They do **not** represent an authenticated Microsoft account and do not replace Minecraft ownership or authenticated online play.

## Microsoft account status

Microsoft/Xbox/XSTS/Minecraft Services sign-in is planned. UN_Nexo will use only its own approved Microsoft application registration; it will not reuse or impersonate another launcher's Client ID. Until that registration is approved, the UI keeps Microsoft sign-in unavailable rather than presenting a broken or misleading login flow.

## Project layout

```text
src/
├─ UN.Nexo.Core/       Launcher/domain logic with no UI dependency
└─ UN.Nexo.Desktop/    Avalonia desktop application

tests/
└─ UN.Nexo.Core.Tests/ Dependency-free regression executable used by CI
```

## Build and test

Requirements:

- .NET 10 SDK

```bash
dotnet restore UN_Nexo.sln
dotnet build UN_Nexo.sln -c Release
dotnet run --project tests/UN.Nexo.Core.Tests/UN.Nexo.Core.Tests.csproj -c Release --no-build
dotnet run --project src/UN.Nexo.Desktop/UN.Nexo.Desktop.csproj
```

## Current limitations

- Microsoft account sign-in is not enabled yet
- Fabric, Forge and NeoForge installers are not included yet
- HTTP Range resume for partially downloaded files is not implemented yet
- Mod/resource-pack/shader management is not implemented yet
- Linux CI uses Ubuntu/Xvfb; Linux Mint/Cinnamon still needs real-device acceptance testing

## Next milestones

1. Complete Microsoft AppID approval and authenticated Microsoft/Xbox/XSTS/Minecraft Services login
2. Add practical launch controls: memory selection, custom JVM options and better Java/runtime diagnostics
3. Harden real-game launch compatibility across legacy and current releases
4. Add resumable downloads, retries/backoff, speed reporting and cancel/retry UX
5. Add instance actions such as duplicate, rename, delete, open game folder and open logs
6. Add Fabric, Forge and NeoForge support
7. Add mod/resource-pack/shader management
8. Add app/package icons, signing and updater

UN_Nexo is under active development and its releases are currently marked as prereleases.
