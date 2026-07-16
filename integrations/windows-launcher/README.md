# Goose Windows Launcher

This directory contains the native Windows host shipped as part of the customized Goose product. It owns Explorer integration, the pre-warmed WinUI task overlay, the single Goose notification-area icon, Windows startup, and Launcher-only settings.

Goose Desktop owns every GUI session and running turn. Desktop-mode submissions use a private current-user activation channel; the Launcher never starts `goose acp`, owns a session, handles tool permission requests, or transfers a turn through a deep link.

## Current vertical slice

- Explorer `IExplorerCommand` for a directory, directory background, or up to eight sibling files.
- Resident per-user `GooseLauncher.exe` with a pre-created hidden WinUI 3 overlay.
- Explorer-to-Launcher and Launcher-to-Desktop length-prefixed named-pipe protocols.
- Desktop readiness/capabilities handshake, cold-start retry, request deduplication, and accepted/rejected acknowledgements.
- Desktop `run` activation with cwd, prompt, files, and bring-to-front behavior; task data is not placed in Desktop command-line arguments or deep links.
- Native Goose tray with Open Goose Desktop, Open Goose CLI, Settings, and Exit Goose.
- Terminal mode remains a direct interactive Goose CLI launch.
- One versioned MSIX containing Goose Desktop, the bundled CLI, Launcher, and Explorer COM integration.

## Develop

Requirements: Windows 10/11, .NET 8 SDK or newer, and the Goose Desktop UI toolchain from the repository root.

```powershell
dotnet build .\GooseLauncher.slnx
dotnet run --project .\tests\GooseLauncher.Core.Tests
dotnet run --project .\src\GooseLauncher.App -- --folder "$PWD"
```

Launcher resolves Goose Desktop and CLI only from the unified installation layout. Set `GOOSE_WINDOWS_ROOT` only when running Launcher from a development build outside the package.

Use the unified Windows build entry point from this directory:

```powershell
.\scripts\build-windows.ps1 -Mode Quick
.\scripts\build-windows.ps1 -Mode Quick -Install
.\scripts\build-windows.ps1 -Mode Full -Sign
.\scripts\build-windows.ps1 -Mode Full -Install -RestartExplorer
```

`Quick` checks Rust and Desktop inputs and reuses the existing CLI or packaged Desktop when they are current. It still rebuilds Launcher and the MSIX so Launcher-only changes are included. `Full` always runs the complete Cargo and Electron packaging stages, while retaining their normal incremental caches. Use `-PlanOnly` with either mode to inspect what will run and validate required tooling without producing an artifact.

The entry point configures the Visual Studio x64 environment, Ninja, UTF-8 C++ compilation, LLVM `libclang`, CMake, and Windows SDK tools before a complete CLI build. Missing dependencies fail early with an actionable installation hint. The lower-level commands remain available when debugging an individual stage:

```powershell
cd ..\..\ui\desktop
pnpm run package
cd ..\..\integrations\windows-launcher
.\scripts\build-msix.ps1
```

Desktop packaging prepares the Windows runtime before Electron packaging. The MSIX build refuses to continue unless the packaged runtime contains Goose CLI, `uv`, `uvx`, and the portable Node `npx` bootstrapper.

See [docs/architecture.md](docs/architecture.md) for protocol and ownership boundaries.
