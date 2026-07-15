# Windows integration architecture

## Process ownership

1. **Explorer command** resolves the selected folder or up to eight sibling files. It sends the context to the resident Launcher pipe. On cold start it launches the sibling `GooseLauncher.exe` without task data, waits for readiness, and then sends the same pipe frame.
2. **Launcher host** is one process per Windows user. It owns the pre-warmed overlay, Explorer activation listener, native Goose tray, startup registration, and Launcher-only settings.
3. **Goose Desktop** hosts a private activation broker. A `run` request always creates a new Desktop window, backend lease, and session with the requested cwd and an auto-submitted initial message.
4. **Goose renderer/backend** keeps its existing ACP WebSocket path, so agent chunks, tool cards, permission requests, cancellation, and errors remain native Desktop behavior from the first response chunk.

Terminal mode is independent: it launches the bundled Goose CLI with the selected cwd. Because the established CLI mode uses `goose run --text`, its task text is visible in that CLI process command line; Desktop-mode privacy guarantees do not rely on that path.

## Desktop activation protocol

The endpoint metadata is stored below `%LOCALAPPDATA%\Goose\launcher` and contains a randomized named-pipe endpoint, protocol version, process id, and per-process authentication token. The user-profile ACL, non-global randomized endpoint, Node pipe permissions, and authentication token jointly restrict activation to the current user.

Frames use a four-byte little-endian payload length followed by UTF-8 JSON. Version 1 supports `ping`, `capabilities`, `run`, and `open`. Requests include `protocolVersion`, `requestId`, `action`, `cwd`, `prompt`, `files`, and `bringToFront`; the authentication token is transport metadata. Payloads are limited to 256 KiB, prompts to 64 KiB, and files to eight sibling paths.

The Desktop validates the protocol version, payload shape, directory, every file, and the authentication token. It caches acknowledgements by request id so a Launcher retry cannot create duplicate sessions. The Launcher hides the overlay only after an `accepted` acknowledgement.

No prompt or selected-file path is placed in a Desktop command line, Goose URI, or diagnostic log. The only cold-start argument is `--launcher-activation`.

## Lifecycle boundaries

Closing the overlay never exits the Launcher. Closing all Desktop windows follows Goose Desktop's Windows lifecycle and exits Desktop while the Launcher remains available. The Launcher does not interpret Desktop turn state; safe handling of closing a window with a running turn is a separate Desktop lifecycle phase.

On Windows the Electron tray and its setting are disabled. The native Launcher tray is therefore the product's only Goose notification-area icon.
