# Windows Launcher rules

## Ownership

- Launcher owns Explorer activation, the native overlay, the Windows tray, startup registration, and Launcher settings.
- Desktop owns ACP sessions and turns. Launcher must not start `goose acp`, proxy turn events, or interpret agent state.
- A Launcher pipe ACK means the UI displayed the activation or accepted it into the bounded in-process queue. A Desktop `accepted` response means the renderer created the session and started the first submission.

## Privacy and protocols

- Never put prompts or selected paths in process arguments, URIs, notifications, or diagnostic logs.
- Keep named-pipe frames length-prefixed and bounded. Preserve authentication, request deduplication, and current-user pipe restrictions.
- Update C#, TypeScript, and C++ protocol consumers together when changing wire fields or constants.

## Packaging

- Build the unified product through `scripts\build-windows.ps1`; use `scripts\build-msix.ps1` only for packaging-stage debugging.
- Package identities and publisher rules are documented in `docs\updates.md`. Do not rename identities or enable Electron's updater for MSIX installs.
- Standard and CUDA packages are mutually exclusive even though their identities differ.

## Verification

```powershell
dotnet test .\tests\GooseLauncher.Core.Tests\GooseLauncher.Core.Tests.csproj -c Release
msbuild .\src\GooseLauncher.ShellExtension\GooseLauncher.ShellExtension.vcxproj /p:Configuration=Release /p:Platform=x64
cd ..\..\ui\desktop
pnpm run test:run -- src/launcherActivation
pnpm run lint:check
```
