# Modrinth integration

UN_Nexo integrates with the public Modrinth API for per-instance mod discovery and installation.

## Public-only design

The normal browse, version lookup, hash matching and download flow does not require a private API token. UN_Nexo does not embed a Modrinth credential and does not depend on a private proxy or proprietary backend for this feature.

The launcher identifies itself to Modrinth with a unique `User-Agent` that includes the UN_Nexo version and public repository identity.

## Compatibility filtering

Searches are filtered by:

- project type: mod
- the selected instance's Minecraft version
- the selected instance's loader (Fabric, Forge, NeoForge or Quilt)

The current instance remains the installation boundary. Downloaded files are verified against Modrinth's declared size and SHA-1 before being passed through the existing `InstanceModService` path-safety and mutation logic.

## Installed-file matching and updates

UN_Nexo hashes local managed JARs and uses Modrinth's public version-file hash endpoint to identify projects and installed versions.

Updates are never applied silently. Selecting an installed project checks the latest compatible version and changes the explicit action to **Update selected** when a newer compatible Modrinth version is available. Disabled mods remain disabled after an explicit update.

## Safety limits

- Modrinth metadata responses are bounded before JSON parsing.
- Mod downloads are limited to 512 MiB.
- Download URLs must use HTTPS and the official `cdn.modrinth.com` host.
- Provider filenames must be plain `.jar` filenames with no traversal or directory components.
- Download size and SHA-1 must match provider metadata before publication.
- A failed download or checksum verification does not publish a mod into the instance.

Local **Install JAR…** remains available independently of Modrinth.


## Dependency plans

Before a Modrinth install/update mutates the instance, UN_Nexo resolves the selected version's dependency graph.

- `required` dependencies are resolved recursively for the same Minecraft version and loader and are installed automatically.
- `optional` dependencies are surfaced in the plan but are not installed automatically.
- `incompatible` relationships are treated as conflicts when they target another project/version selected by the same plan.
- `embedded` relationships do not produce a separate install.
- Project-only and version-pinned dependency edges are both supported.
- Cycles, conflicting pinned versions and oversized dependency graphs fail before publication.

All files that need to change are downloaded and verified into staging first. The resulting file set is then published under a single instance operation lease. If publication fails part-way through, newly published files are rolled back and replaced files are restored, so Play/backup/repair cannot observe a partially installed required-dependency set.
