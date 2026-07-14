# Architecture and compatibility notes

## Layers

1. **Explorer activation**: packaged `IExplorerCommand` resolves a directory, directory background, or up to eight files with one shared parent. It captures the pointer position, attempts the current-user named pipe, then uses `goosecompanion://show` for cold start.
2. **Companion shell**: one on-demand WPF process owns the overlay, protocol parsing, and pipe listener only while a task is being entered or run. It has no tray icon; Goose Desktop remains the sole resident tray application.
3. **ACP bridge**: a newline-delimited JSON-RPC client launches the Goose CLI with `acp`, negotiates ACP v1, creates a session for `cwd`, streams session updates, handles tool calls and permissions, and cancels a turn.
4. **Goose Desktop**: remains the owner of providers, credentials, extensions, policy, persistent conversations, and full UI.

## Deliberate boundaries

There is no model/provider UI, worker runtime, attachment parser, chat history store, Markdown client, task queue, diff/backup system, or custom permission policy here. Selected files are sent as ACP resource links and a concise text context; Goose performs the work.

There is also no Companion settings surface. Goose 1.42 handles `new-session`, `resume`, `sessions`, `extension`, `bot`, and `recipe` deep links, but has no `goose://settings` route. The fallback button opens Goose Desktop so its onboarding/settings own configuration. The minimal upstream request is a stable `goose://settings` deep link (optionally with a settings section), not a duplicate settings implementation here.

## Compatibility risks

- Goose 1.42 accepts ACP v1 over stdio. The bridge accepts current snake_case update names and older camelCase aliases so it can tolerate nearby Goose releases.
- ACP sessions are loadable by id, and Goose Desktop 1.36+ supports `goose://resume/<sessionId>`. `Open in Goose` uses that route after the prompt finishes; it never makes Desktop race the Companion for a running session.
- The Companion ACP subprocess does not attach to Desktop's private in-memory `goose serve`; it writes the same durable Goose session store. This was verified on Goose 1.42: an ACP-created session appeared as `session_type: user` with the correct cwd and was resumable by Desktop. Seamless mid-turn takeover is not supported, so hand-off occurs only after the turn completes. A future single-host design would require a small, public Desktop activation/ACP broker API; merely copying Launcher sources into a Goose fork would not provide it.
- The native shell extension is present and registered by the MSIX manifest, but its build requires MSVC v143 and a Windows 10/11 SDK. The managed vertical slice builds with the .NET SDK alone.
- The pipe is restricted to the current Windows user, validates length-prefixed payloads, and bounds file count. Cold activation is length-bounded to avoid oversized URI invocation.
- WPF provides IME and high-contrast behavior through native controls. The app declares Per-Monitor V2 awareness and repositions within the target monitor work area; cross-monitor drag/resize still needs physical multi-monitor QA.
