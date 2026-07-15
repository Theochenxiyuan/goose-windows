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
- MSIX and Explorer COM scaffolding; unified Goose Desktop/CLI/Launcher packaging remains a later release phase.

## Develop

Requirements: Windows 10/11, .NET 8 SDK or newer, and the Goose Desktop UI toolchain from the repository root.

```powershell
dotnet build .\GooseLauncher.slnx
dotnet run --project .\tests\GooseLauncher.Core.Tests
dotnet run --project .\src\GooseLauncher.App -- --folder "$PWD"
```

Blank executable settings first resolve Goose Desktop and CLI from the unified installation layout. Explicit path overrides remain available for development and advanced use.

See [docs/architecture.md](docs/architecture.md) for protocol and ownership boundaries.
