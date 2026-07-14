# Goose Launcher

Goose Launcher is a small Windows companion for [Goose](https://github.com/aaif-goose/goose). It adds an Explorer context-menu entry and a native task overlay while leaving models, credentials, extensions, history, and the full conversation UI in Goose.

## Current vertical slice

- Windows App SDK / WinUI 3 launcher overlay with folder/file context and a keyboard-first task input.
- Per-user single instance and named-pipe warm activation; `goosecompanion://show` cold activation.
- Goose discovery (including the CLI bundled with Goose Desktop) and ACP v1 over stdio.
- Explorer `IExplorerCommand` and MSIX manifest/build scaffolding.
- A dependency-free core test executable.

The Companion is deliberately on-demand and has no tray icon. After ACP confirms that the session exists and the prompt request has been written, the overlay opens that session in Goose Desktop and disappears. The hidden Companion keeps only the active ACP turn alive, resurfaces solely if ACP needs a permission decision, and exits when the turn ends. Goose Desktop remains the only resident tray/configuration host.

## Develop

Requirements: Windows 10/11, .NET 8 SDK or newer, and Goose Desktop/CLI.

```powershell
dotnet build .\GooseLauncher.slnx
dotnet run --project .\tests\GooseLauncher.Core.Tests
dotnet run --project .\src\GooseLauncher.App -- --folder "$PWD"
```

The app locates Goose in `PATH`, `GOOSE_CLI_PATH`, and common Desktop install locations such as `resources\bin\goose.exe`.

## ACP and Desktop hand-off

The Companion owns a `goose acp` stdio process and creates a durable ACP session with the selected working directory. Goose 1.42 advertises `session/load`; the Companion waits for `session/new`, writes `session/prompt`, and immediately uses Goose Desktop's `goose://resume/<sessionId>` deep link. It does not render the response. The minimum Desktop version for this hand-off is Goose 1.36.

See [docs/architecture.md](docs/architecture.md) for boundaries and known risks.
