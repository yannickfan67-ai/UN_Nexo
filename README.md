# UN_Nexo

A modern, lightweight and extensible Minecraft launcher built with **C#**, **.NET 10** and **Avalonia 12**.

UN_Nexo is in early development. The goal is to keep the launcher core independent from the UI so Minecraft installation, accounts, mod loaders and launch logic can evolve without turning the desktop project into a monolith.

## Current milestone — v0.1.0-dev

Implemented in the first development pass:

- Avalonia desktop shell with a dark Nexo dashboard
- Cross-platform Minecraft directory detection
- Nexo data and instance directory management
- Java discovery through `JAVA_HOME`, `PATH` and common platform install locations
- Java version probing
- Mojang version-manifest lookup for the latest release and snapshot
- JSON-backed instance metadata store
- Windows + Linux CI build matrix

## Project layout

```text
src/
├─ UN.Nexo.Core/       Launcher/domain logic with no UI dependency
└─ UN.Nexo.Desktop/    Avalonia desktop application
```

## Build

Requirements:

- .NET 10 SDK

```bash
dotnet restore UN_Nexo.sln
dotnet build UN_Nexo.sln -c Release
dotnet run --project src/UN.Nexo.Desktop/UN.Nexo.Desktop.csproj
```

## Roadmap

The next launcher milestones are:

1. Minecraft version installation and download pipeline
2. Asset, library and native dependency resolution
3. Per-version Java selection and JVM argument builder
4. Microsoft/Xbox/Minecraft authentication
5. Actual game process launch and log console
6. Fabric, Forge and NeoForge support
7. Mod/resource-pack/shader management
8. Update channel and self-update support

## Status

This repository is under active development and is **not yet a production-ready Minecraft launcher**.
