# UN_Nexo

Independent Minecraft: Java Edition file manager for Windows and Linux, built with C# / .NET 10 / Avalonia 12.

> **NOT AN OFFICIAL MINECRAFT PRODUCT. NOT APPROVED BY OR ASSOCIATED WITH MOJANG OR MICROSOFT.**

## Current release: 0.9.0-download-only

This release **only downloads and manages game files**. It has no account page, Microsoft sign-in, offline identities, game execution, server connection, or automatic restoration of those features. Downloading files does not grant a Minecraft license. To play, use a launcher that authenticates the player and verifies their Java Edition ownership.

### Included

- Native desktop download workspace with Traditional Chinese labels and a flat tile layout.
- Full official version catalog, search, release/snapshot/historical filters, and cached catalog browsing when offline.
- Named, isolated Vanilla instances; existing instance files and worlds remain in their existing locations.
- Persistent download queue, manual pause/resume, per-task retry, transfer details and failure messages.
- SHA-1 and size validation of official metadata, client, libraries, native archives, asset indexes and asset objects.
- Repair reuses verified files and downloads only missing/corrupt components. Worlds, mods and user configuration are preserved.
- Official download source by default; optional BMCLAPI acceleration with official fallback and the existing trusted-redirect policy.
- Local instance renaming and folder access.
- Exportable JSON download report containing only version IDs, task states and timestamps. Account caches, personal instance names, error messages, local paths and game files are excluded.
- MIT license for original project code, independent-product notice and public issue tracker.

On restart, queued or active transfers recover **paused**, awaiting an explicit Resume queue. PC retries reuse complete verified files; a partially transferred individual file is downloaded again. HTTP Range resume is not claimed for this PC release.

### Distribution boundary

The desktop project now references `src/UN.Nexo.Downloads`, an explicit source allowlist. Its output contains the verified installer, metadata/rules, safe storage and download-state management. It **does not reference MSAL** and does not compile the account store, authentication services, Minecraft launch planner or game process runner. Previous launcher UI files are excluded from the desktop build. There is no review-detection switch or runtime setting to enable gameplay.

`src/UN.Nexo.Core` and the earlier launcher UI source remain in the repository for regression coverage and development history. They are **not dependencies of the distributed download-only application**. Their previous launch/runtime/server features must not be presented as features of this release.

## Build and validation

Requires .NET 10 SDK.

```sh
dotnet restore UN_Nexo.sln
dotnet build UN_Nexo.sln -c Release
dotnet run --project tests/UN.Nexo.Downloads.Tests -c Release
dotnet run --project tests/UN.Nexo.Core.Tests -c Release
dotnet run --project src/UN.Nexo.Desktop
```

`UN.Nexo.Downloads.Tests` checks the shipping assembly boundary, interrupted-state recovery, report redaction, catalog persistence, actual installer publication, verified-cache reuse and repair of same-size corruption. The legacy core regression suites continue to protect the shared installer source.

Release CI builds self-contained Windows x64 and Linux x64 packages. Linux still requires its normal desktop libraries. Platform artifacts and CI results are available from this repository's Releases and Actions pages.

## Future gameplay and AppID review

The previously registered UN_Nexo public Client ID is `ac6485d3-1fd4-42d1-89c0-40ffee68d915`. This release does not use it or call authentication APIs. See [the review statement](docs/download-only-review.md) for current scope and the intended future authentication flow. An allowlist request is not approval; only Mojang/Microsoft can grant it. Any future gameplay release must require the player's Microsoft authentication, Minecraft entitlement and Java profile before gameplay or server access.

The Android companion is a separate project: [Avero-Launcher](https://github.com/yannickfan67-ai/Avero-Launcher).

## Maintainer, privacy and license

Maintained independently by Yannick Fan / `yannickfan67-ai`. Contact: [GitHub Issues](https://github.com/yannickfan67-ai/UN_Nexo/issues).

See [PRIVACY.md](PRIVACY.md), [LICENSE](LICENSE), [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md), and [CHANGELOG.md](CHANGELOG.md). The MIT license does not cover Minecraft game files, trademarks, or third-party dependencies. Minecraft files are obtained directly from their distribution endpoints when the user requests them; they are not bundled in release packages.
