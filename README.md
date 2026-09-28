# Codex Backdrop

A small Windows tray app that puts an image, animated GIF or video behind the official Codex desktop app. Codex's own interface stays sharp on top: text, menus, the composer and the sidebar are drawn by Codex itself, with the wallpaper showing through its canvas.

It does not modify Codex, its files, its settings or its network traffic. Sibling project: [Claude Backdrop](https://github.com/okisooo/claude-backdrop) does the same for the Claude desktop app, with the same tray menu.

## Install

Requirements: Windows 10 2004 or later (Windows 11 recommended), the Codex desktop app from the Microsoft Store, the [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0), and the .NET 9 SDK to build.

```powershell
.\install.ps1
```

This builds the app, installs it to `%LOCALAPPDATA%\Programs\CodexBackdrop`, adds a **Codex Backdrop** Start menu entry and starts it. No administrator rights are needed. `.\uninstall.ps1` removes it again (add `-RemoveSettings` to also delete settings).

To only build: `.\build.ps1` (output in `artifacts\publish`).

## Use

The app lives in the system tray. Right-click the icon for:

- **Choose background…** PNG, JPG, WebP, GIF, MP4 or WebM. Double-clicking the tray icon does the same.
- **Readability wash**: how strongly Codex's canvas tints the wallpaper, so text stays easy to read.
- **Blur** and **Fit** (cover or stretch).
- **Calm behind content**: extra tint behind floating surfaces such as the composer: Off, Light, Medium (default) or Strong.
- **Animate GIFs** on or off. Videos always play.
- **Wallpaper behind sidebar**: off keeps the sidebar and title bar solid.
- **Attach now**: restarts Codex once with the backdrop (or opens it), instead of waiting for the next launch.
- **Pause**, **Start with Windows**, settings folder and log.

Pause takes the backdrop out of Codex without restarting it. Exit does the same and leaves Codex running.

On first run the app picks up the wallpaper and look from Claude Backdrop if it is installed.

Settings are stored in `%LOCALAPPDATA%\CodexBackdrop\config.json`, the log in `log.txt` next to it. `config.json` also holds a few values the menu does not show: `backgroundOpacity`, `brightness`, `saturation`, `playbackRate`, `mediaPosition`, `devToolsPort` and `rendererProfilePath`.

## How it works

Codex is a Chromium app, so the backdrop is drawn inside its page instead of over it:

1. **Launch.** Codex is started through Windows package activation, the same path as the Start menu, with a Chromium debugging port bound to `127.0.0.1` and a separate renderer profile (Chromium only allows the port on a non-default profile). If Codex is already open without the backdrop, the app restarts it once, closing only the exact Codex process tree it identified.
2. **Inject.** Over that local port a stylesheet and a background layer are added to Codex's page. The wallpaper sits behind the app, Codex's canvas tints it (readability wash), floating surfaces get local glass (calm), and the sidebar, title bar and the thin window frame around the page stay solid unless *Wallpaper behind sidebar* is on. Menus, dialogs and tooltips keep Codex's own styling; an in-page check guards any whose text would become hard to read against the wallpaper.
3. **Keep.** The injector re-checks the page every few seconds and restores the layer if Codex rebuilds its document. A supervisor attaches whenever Codex opens: at most one controlled restart per launch, and a failed attempt is not retried until *Attach now* or a newer Codex version.

All Codex-specific selectors live in [`src/CodexBackdrop/Runtime/codex-selectors.css`](src/CodexBackdrop/Runtime/codex-selectors.css), built into the exe. A `Runtime` folder next to `CodexBackdrop.exe` overrides the built-in copy, so a fix for a new Codex version can be tried without a rebuild.

The app handles:

- **Slow first starts after a Codex update:** it waits while Codex renders its first screen instead of counting that as a failure, and reports a launch that exits straight away.
- **Settings changes:** applied at once, without restarting Codex.
- **A minimized or busy Codex:** checks back off while its page does not answer.
- **Light and dark themes:** both palettes are covered; the text check follows the current theme.

## Limitations

- The first attach restarts Codex once. Codex hides to the tray when its window is closed, so it cannot be asked to quit and is stopped instead; anything running inside Codex at that moment is interrupted.
- While attached, Codex uses a separate Chromium profile, so web sessions inside Codex (the built-in browser, for example) are separate from the ones in normal mode.
- A Codex update that changes its page structure can pause the backdrop until the selectors are updated. Codex itself keeps working and simply shows without the wallpaper.
- Only Codex's main window gets the backdrop; pop-out windows stay plain.

## Troubleshooting

- The tray menu shows the current state.
- **Open log** shows errors, each launch and attachment, and every restart with the reason for it.
- `tools\verify.ps1` builds the app and runs the offline checks (the .NET policy suites and headless-browser checks of the injected page code). They need Node.js 22 and Chrome or Edge.
- `node tools\live-theme-check.mjs` connects to a running, attached Codex and checks the light and dark palettes.

## Security

The local debugging endpoint is powerful. It is bound to `127.0.0.1` and the wallpaper is handed to the page directly, never served over HTTP, but another program running as your Windows user can connect to it while the backdrop is attached. **Pause**, then start Codex from the Start menu, for a session without it.

## Privacy

The app talks only to the Codex window on your own machine. The wallpaper stays on your PC; nothing is saved or sent anywhere.

## License

MIT. Not affiliated with or endorsed by OpenAI. Codex and ChatGPT are trademarks of OpenAI.
