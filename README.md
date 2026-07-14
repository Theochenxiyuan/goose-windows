# Goose Launcher

Goose Launcher is a small Windows companion for [Goose](https://github.com/aaif-goose/goose). It adds an Explorer context-menu entry and a native task overlay while leaving models, credentials, extensions, history, and the full conversation UI in Goose.

## Current vertical slice

- Windows App SDK / WinUI 3 launcher overlay with folder/file context and a keyboard-first task input.
- Resident notification-area host, pre-created hidden overlay, per-user single instance, and named-pipe warm activation; `goosecompanion://show` remains the cold-start fallback.
- Goose discovery (including the CLI bundled with Goose Desktop) and ACP v1 over stdio.
- A small Companion settings window for CLI/Desktop path overrides, Desktop/Terminal task target, and start-with-Windows registration.
- Explorer `IExplorerCommand` and MSIX manifest/build scaffolding.
- A dependency-free core test executable.

The Companion stays resident after launch so Explorer activations only update and reveal an already-created WinUI window. Closing or submitting the overlay hides it; only **Exit** in the tray menu terminates the process. The tray menu provides New task, Open Goose, Settings, and Exit. Goose still exclusively owns models, credentials, extensions, agent policy, and conversation history.

## Develop

Requirements: Windows 10/11, .NET 8 SDK or newer, and Goose Desktop/CLI.

```powershell
dotnet build .\GooseLauncher.slnx
dotnet run --project .\tests\GooseLauncher.Core.Tests
dotnet run --project .\src\GooseLauncher.App -- --folder "$PWD"
```

The app locates Goose in `PATH`, `GOOSE_CLI_PATH`, and common Desktop install locations such as `resources\bin\goose.exe`.

In **Goose Desktop** mode, the Companion creates a durable ACP session and opens it with `goose://resume/<sessionId>`. In **Terminal** mode, it starts `goose run --text <prompt> --interactive` in Windows Terminal with the selected working directory. Both modes include the exact selected file paths in the task context.

## ACP and Desktop hand-off

The Companion owns a `goose acp` stdio process and creates a durable ACP session with the selected working directory. Goose 1.42 advertises `session/load`; the Companion waits for `session/new`, writes `session/prompt`, and immediately uses Goose Desktop's `goose://resume/<sessionId>` deep link. It does not render the response. The minimum Desktop version for this hand-off is Goose 1.36.

See [docs/architecture.md](docs/architecture.md) for boundaries and known risks.
