# UN_Nexo

UN_Nexo is an independent, cross-platform Minecraft: Java Edition launcher built with **C#**, **.NET 10** and **Avalonia 12**.

The launcher core is kept separate from the desktop UI so installation, accounts, launch logic and future mod-loader support can evolve without turning the UI project into a monolith.

> UN_Nexo is not an official Minecraft product and is not approved by or associated with Mojang or Microsoft.

## Current milestone — v0.8.1-dev

The current development line includes:

- Windows and Linux desktop UI with Home, Instances, Accounts, Servers, Runtime, Repair and Settings access
- Lightweight cold-start splash and main-window fade-in without heavy GPU effects
- Bundled Avalonia Inter plus Linux Noto CJK fallback dependencies
- Compact in-app game-session status overlay with separate **starting** and **running** states
- Persistent per-instance launch traces, crash diagnosis and bounded sanitized diagnostic ZIP export for stuck/failed launches
- Diagnostic export accepts only explicitly trusted log roots, rejects symlink/reparse-point source paths and revalidates authorization before every read
- Supervised stdout/stderr/heartbeat workers that stop and reap the Java process tree if launch diagnostics or output handling fails
- Terminal launch results remain visible after the session ends instead of being immediately replaced by a generic Ready message
- Mojang version catalog with release history and recent snapshots
- Per-instance Vanilla preparation with client, libraries, natives and assets
- Safe instance rename and two-step deletion, with backups preserved by default and optional explicit backup removal
- Hardened instance deletion uses a tombstone publication boundary and never follows nested symlink/junction/reparse entries or linked backup roots
- Loader rollback state, Fabric/Quilt profile paths, Fabric/Quilt Maven publication/downloads and mod enable/remove mutations enforce bounded physical managed-file boundaries
- Forge/NeoForge installer stdout/stderr and Java `-version` probes use bounded concurrent capture instead of retaining unbounded child-process output
- First-class Fabric, Quilt, Forge and NeoForge instance preparation/launch with automatic pre-launch preparation and inherited Java/profile handling
- Quilt, Forge and NeoForge managers support compatible loader discovery plus create, prepare and repair flows using their official metadata/distribution paths
- One-click instance integrity checking and repair for install state, metadata, client JAR, libraries, asset index/objects, extracted natives, required Java and Linux runtime dependencies
- Repair verifies Mojang SHA-1 metadata, rejects unsafe metadata paths/hashes, keeps verified files and reacquires only missing or corrupt Vanilla components
- Repair preserves worlds, mods, resource packs, screenshots and ordinary configuration; legacy virtual assets and pre-1.6 resources are rebuilt after object repair
- Official downloads or BMCLAPI acceleration with automatic official fallback and SHA-1 verification
- Response-body idle timeouts so a source that returns headers and then stops sending data cannot leave preparation stuck forever
- Explicit user cancellation remains cancellation and is not silently converted into a source retry
- Same-instance Prepare, Repair, Play, Backup/Restore and mod mutations share one operation coordinator, including cooperating Nexo processes
- Persistent account/server stores, instance-name creation and settings publication are serialized across Nexo processes to avoid lost updates
- Offline local profiles plus Microsoft account profiles, kept as distinct account types
- Java discovery through `JAVA_HOME`, `PATH` and common platform install locations
- Automatic exact-major Java selection from Minecraft version metadata
- Automatic SHA-256-verified Eclipse Temurin runtime acquisition when a required Java major is missing
- Nexo-managed cached Java runtimes are checked with a bounded `java -version`/architecture probe before first reuse in a launcher session
- Stalled Adoptium metadata/runtime transfers are bounded instead of waiting indefinitely after HTTP headers
- Runtime & Diagnostics panel with selected-instance Java requirement inspection
- Auto or explicit game-memory limits instead of a fixed 2 GB heap
- Validated extra JVM arguments persisted separately from ordinary launcher settings
- Launcher-managed `-Xmx`, classpath and `-jar` options protected from accidental overrides
- Vanilla launch-plan generation for modern `arguments` metadata and legacy `minecraftArguments`
- Verified compatibility with modern 1.21.4, legacy 1.8.9 and real Minecraft 1.5.2 startup paths
- Minecraft 1.5.2 `pre-1.6` / `map_to_resources` asset materialization for legacy `resources/` layouts
- Safe process launching through `ProcessStartInfo.ArgumentList`, including paths containing spaces
- Per-launch stdout/stderr logs, exit-code reporting and Stop game support
- Quick access to selected-instance game and launcher-log folders
- Provider-neutral mod browser for Modrinth and CurseForge, with compatible recommendations, dependency planning and atomic multi-mod publication
- Installed-mod matching/update checks preserve disabled state and coordinate with the same per-instance operation lease
- Server Hub with persistent favorites and validated host/port/IPv6 addresses
- Version-aware direct multiplayer launching: Quick Play when advertised by modern metadata, legacy server/port arguments for older versions
- Linux DEB/RPM and Windows self-contained EXE prerelease packages
- Branded Nexo application/package icons for Windows executables and Linux desktop menus
- Debian/Mint packages include the `libXtst` runtime needed by Java 8/LWJGL 2; CI checks `libXtst.so.6`, CJK fallback and the final packaged UI
- Build CI cancels superseded runs and has bounded job timeouts so a hung regression cannot occupy runners indefinitely

Local profiles are intended for local worlds, development and servers that explicitly accept that login mode. They do **not** represent an authenticated Microsoft account and do not replace Minecraft ownership or authenticated online play.

## Instance check and repair

The Repair utility can inspect the selected Vanilla instance without launching it. It checks the install marker, version metadata, client JAR, libraries, native archives and extracted files, asset index and asset objects, the required Java major, and the Linux libXtst dependency when relevant.

Where Mojang metadata provides a SHA-1, Nexo verifies it instead of trusting file presence alone. Metadata-controlled relative paths and SHA-1 values are validated before they are followed, so a damaged or malicious local metadata file cannot make Repair walk outside the expected game directories.

Repair reuses already verified files and runs the normal verified installer for missing/corrupt game components. It then ensures the required Java runtime is usable and reacquires a managed Temurin runtime when necessary. Historical virtual assets and current pre-1.6 `map_to_resources` layouts are rebuilt from verified object files after repair. User worlds, mods, resource packs, screenshots and ordinary game configuration are outside the repair write set.

## Launch debug tracing

Nexo writes a `launch-trace-*.log` beside the selected instance's normal launcher logs. The trace starts before preparation/Java selection and records launch phase changes, the selected Java executable, working directory, heap setting, main class, native path, classpath entry count and platform architecture. It intentionally does not dump authentication tokens or the complete command line.

After Java starts, a watchdog emits a heartbeat every 10 seconds with the Java PID, process lifetime, time since the last stdout/stderr line and working-set memory when available. The trace also records how long Java took to start, how long it took to produce the first Minecraft output, process-start exceptions and the final exit code.

The game-session overlay only shows its indeterminate progress bar while Nexo is actually in the starting phase. Once the Java process exists, the overlay switches to process-health information instead of looking permanently stuck on “Launching”. The overlay also provides an **Open logs** action.

The process runner also treats failures in start reporting, stdout/stderr consumers or the heartbeat worker as launch failures. Nexo terminates the whole Java process tree, waits for it to be reaped, observes the remaining workers and then releases logging resources. This prevents a failed redirected-output consumer from leaving Java blocked on a full pipe or keeping the launcher stuck in a running state.

Per-instance traces and process logs are kept under:

```text
UN_Nexo/instances/<instance-id>/launcher-logs/
```

## Legacy compatibility status

Minecraft 1.5.2 has been exercised against the current official Mojang metadata and files, not only synthetic fixtures. The live smoke downloaded the real 1.5.2 client, libraries, natives and assets, mapped 749 `pre-1.6` resource entries into the legacy `resources/` layout, extracted six native files, acquired a managed Temurin Java 8 runtime, launched through LaunchWrapper/LWJGL 2.9.0 and stayed alive for the full 45-second observation window.

A later real Ubuntu 24.04 Minecraft 1.8.9 launch also reached LWJGL and exposed a missing `libXtst.so.6` package dependency. v0.6.4 and later declare that dependency in the Linux packages and verify it during release packaging.

The legacy client may still attempt obsolete Mojang-era network endpoints for some resources, and a headless CI runner has no physical audio device, but those conditions did not prevent the tested 1.5.2 client from reaching its running game process.

## Runtime controls

The Runtime page stores its configuration in `runtime-settings.json`, separate from download-source and account data. Memory may be left at **Auto** or set explicitly from 512 MB to 64 GB. Auto uses a conservative recommendation based on memory available to the launcher process so the desktop keeps headroom.

Extra JVM arguments are tokenized and passed through `ProcessStartInfo.ArgumentList`; they are never executed through a shell. Nexo blocks manual heap-limit overrides (including `-XX:MaxHeapSize`), classpath overrides (including equals-form options), and alternate JAR/module entry points because those are generated by the launcher and Minecraft metadata. JVM argument/option files (`@file`, `-XX:Flags`, `-XX:VMOptionsFile`) are rejected because their contents bypass this validation; enter supported options directly instead.

When the selected Minecraft version requires a Java major that is not present locally, Nexo can acquire a matching x64 Eclipse Temurin JRE, verify its SHA-256 checksum and keep it under Nexo-managed runtime storage for reuse. Existing managed runtimes require valid Nexo metadata and a bounded startup/version/architecture probe before they are trusted for reuse.

## Multiplayer status

Server Hub can save a server address and launch a prepared instance directly toward it. Modern Minecraft versions that advertise Quick Play support use `--quickPlayMultiplayer`; older versions use the legacy `--server` / `--port` path.

Authenticated public/online-mode servers can use a signed-in Microsoft profile. UN_Nexo uses its own registered public Client ID and does not spoof another launcher's identity. Friend-to-friend room-code networking is planned around interoperability with the Terracotta / EasyTier / Scaffolding ecosystem rather than creating another incompatible private protocol.

## Microsoft account status

UN_Nexo now implements Microsoft public-client sign-in through the system browser, then exchanges the Microsoft session through Xbox Live, XSTS and Minecraft Services. Refresh credentials are persisted with the operating system-backed MSAL secure cache; `accounts.json` stores only profile metadata and the non-secret MSAL account identifier. Before an online launch, Nexo silently refreshes the Microsoft session, verifies the Minecraft entitlement/profile and passes the resulting short-lived Minecraft access token only in memory to the launch plan.

The registered UN_Nexo Client ID is `ac6485d3-1fd4-42d1-89c0-40ffee68d915`. Minecraft Services can still reject a newly registered third-party Client ID with `Invalid app registration` until Microsoft/Minecraft authorizes it; Nexo reports that condition explicitly, points to the official [Minecraft AppID review route](https://aka.ms/mce-reviewappid), and will not substitute another launcher's Client ID. See [`docs/microsoft-auth.md`](docs/microsoft-auth.md) for the flow and storage model.

## Project layout

```text
src/
├─ UN.Nexo.Core/       Launcher/domain logic with no UI dependency
└─ UN.Nexo.Desktop/    Avalonia desktop application

tests/
├─ UN.Nexo.Core.Tests/       Launch/download regression executable
├─ UN.Nexo.Stability.Tests/  Timeout/runtime/process stability regressions
└─ UN.Nexo.Repair.Tests/     Cross-platform instance integrity regressions
```

## Build and test

Requirements:

- .NET 10 SDK

```bash
dotnet restore UN_Nexo.sln
dotnet build UN_Nexo.sln -c Release
dotnet run --project tests/UN.Nexo.Core.Tests/UN.Nexo.Core.Tests.csproj -c Release --no-build
dotnet run --project tests/UN.Nexo.Stability.Tests/UN.Nexo.Stability.Tests.csproj -c Release
dotnet run --project tests/UN.Nexo.Repair.Tests/UN.Nexo.Repair.Tests.csproj -c Release
dotnet run --project src/UN.Nexo.Desktop/UN.Nexo.Desktop.csproj
```

## CurseForge provider

The mod browser supports Modrinth without credentials and CurseForge through one of two runtime configurations. No CurseForge API key is compiled into the public repository or release artifacts.

Direct API access:

```bash
export UN_NEXO_CURSEFORGE_API_KEY='your-key'
```

On PowerShell:

```powershell
$env:UN_NEXO_CURSEFORGE_API_KEY = 'your-key'
```

For a server-side proxy such as a Cloudflare Worker, keep the CurseForge key in the server-side secret store and point Nexo at an HTTPS CurseForge-compatible v1 base URL:

```bash
export UN_NEXO_CURSEFORGE_API_BASE='https://your-worker.example/curseforge/v1/'
```

When a custom API base is configured, Nexo does **not** send `x-api-key` to that origin; the proxy is expected to add the credential server-side. Mod downloads still have to resolve to the trusted HTTPS `forgecdn.net` CDN and are SHA-1/size verified before atomic publication. See [`docs/curseforge.md`](docs/curseforge.md).

## Current limitations

- Live Microsoft/Minecraft sign-in still depends on Minecraft Services authorizing the registered UN_Nexo Client ID and completing real-account acceptance testing
- Runtime overrides are global; per-instance memory/JVM overrides are not implemented yet
- Friend-to-friend room-code/P2P networking is not included yet
- Fabric, Quilt, Forge and NeoForge preparation/launch are included; deeper integrity diagnostics remain strongest for Vanilla/Fabric while the other loader managers can re-run their verified preparation/repair path
- HTTP Range resume for partially downloaded files is not implemented yet
- Mod management supports local JARs plus Modrinth and CurseForge mods; resource-pack/shader management is not included yet
- Linux CI uses Ubuntu/Xvfb; Linux Mint/Cinnamon still needs real-device acceptance testing

## Next milestones

1. Add resumable downloads, retries/backoff and stronger cancel/retry UX
2. Complete live Minecraft Services authorization/acceptance for the UN_Nexo Client ID and real-account acceptance testing
3. Add per-instance runtime overrides and richer managed-Java controls
4. Expand backup/restore management beyond the current safe world backup and restore flows
5. Expand loader-specific integrity diagnostics beyond the current verified manager re-preparation/repair flows
6. Extend provider browsing to resource packs/shaders and richer recommendation controls
7. Add Terracotta/EasyTier/Scaffolding-compatible friend room networking after protocol/license review
8. Add package signing and updater

UN_Nexo is under active development and its releases are currently marked as prereleases.
