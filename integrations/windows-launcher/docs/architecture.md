# Architecture and compatibility notes

## Layers

1. **Explorer activation**: packaged `IExplorerCommand` resolves a directory, directory background, or up to eight files with one shared parent. It captures the pointer position, attempts the current-user named pipe, then uses `goosecompanion://show` for cold start.
2. **Companion shell**: one resident WinUI 3 process owns a notification-area icon, a pre-created input overlay, protocol parsing, and the pipe listener. Overlay close/Esc only hides the window; tray Exit owns application shutdown. The tray can open either Goose Desktop or an interactive Goose CLI session, and the overlay exposes the configured task target without submitting a task.
3. **Run targets**: Desktop mode uses the ACP bridge to create and run a durable Goose session before opening it in Goose Desktop. Terminal mode launches `goose run --text ... --interactive` directly with the same cwd and selected-file context, leaving presentation to the configured default Windows terminal.
4. **Goose Desktop**: remains the owner of providers, credentials, extensions, policy, persistent conversations, and full UI.

## Deliberate boundaries

There is no model/provider UI, worker runtime, attachment parser, chat history store, Markdown client, task queue, diff/backup system, or custom permission policy here. Selected files are sent as ACP resource links and a concise text context; Goose performs the work.

The Companion settings surface is intentionally limited to Goose CLI/Desktop executable overrides, Desktop versus Terminal task target, and start-with-Windows registration. It is stored under `%LOCALAPPDATA%\GooseLauncher`; it never stores provider, model, credential, extension, or permission-policy settings. Blank executable fields use automatic discovery.

Start-with-Windows is a per-user Run entry that activates `goosecompanion://tray`, avoiding a version-specific path into an installed MSIX. Startup creates the tray and pre-warms the hidden overlay without presenting a task window.

## Compatibility risks

- Goose 1.42 accepts ACP v1 over stdio. The bridge accepts current snake_case update names and older camelCase aliases so it can tolerate nearby Goose releases.
- Goose 1.42 only expands `file://` ACP resource links when the target decodes as text; binary files such as PNGs are otherwise omitted from the persisted user message. The Companion therefore mirrors every selected file's exact absolute path into the text prompt while retaining resource links for protocol-compatible agents. It does not infer model vision support or parse attachments itself.
- ACP sessions are loadable by id, and Goose Desktop 1.36+ supports `goose://resume/<sessionId>`. The Launcher waits for `session/new` and for the prompt JSON-RPC request to be flushed before opening that route; it never opens an uncreated session.
- The Companion ACP subprocess does not attach to Desktop's private in-memory `goose serve`; it writes the same durable Goose session store. This was verified on Goose 1.42: an ACP-created session appeared as `session_type: user` with the correct cwd and was resumable by Desktop. Opening it mid-turn is a UI hand-off, not an ownership transfer: the hidden Companion still owns that turn and permission requests temporarily reopen its native confirmation panel. A future true live takeover requires a small, public Desktop activation/ACP broker API; merely copying Launcher sources into a Goose fork would not provide it.
- The resident process currently runs one Desktop-mode ACP turn at a time. A second Explorer activation while that turn is active reports the busy state through the tray instead of starting a competing session. Terminal-mode tasks are owned by their terminal process and do not keep the overlay busy.
- The native shell extension is present and registered by the MSIX manifest, but its build requires MSVC v143 and a Windows 10/11 SDK. The managed vertical slice builds with the .NET SDK alone.
- The pipe is restricted to the current Windows user, validates length-prefixed payloads, and bounds file count. Cold activation is length-bounded to avoid oversized URI invocation.
- WinUI 3 provides native IME and high-contrast behavior. The app declares Per-Monitor V2 awareness, uses physical-pixel `AppWindow` positioning, and constrains custom drag/resize to the target monitor work area; cross-monitor behavior still needs physical multi-monitor QA.
