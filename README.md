# Claude Backdrop

A small Windows tray app that puts an image or animated GIF behind the official Claude desktop app. Claude's own interface stays sharp on top: text, buttons, the composer and the sidebar are drawn exactly as Claude draws them, with the wallpaper showing wherever Claude has its plain background.

It does not modify Claude, its files, its settings or its network traffic. Sibling project: [Codex Backdrop](https://github.com/okisooo/codex-backdrop) does the same for the Codex desktop app, with the same tray menu.

## Install

Requirements: Windows 10 2004 or later (Windows 11 recommended), the [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0), and the .NET 9 SDK to build.

```powershell
.\install.ps1
```

This builds the app, installs it to `%LOCALAPPDATA%\Programs\ClaudeBackdrop`, adds a **Claude Backdrop** Start menu entry and starts it. No administrator rights are needed. `.\uninstall.ps1` removes it again (add `-RemoveSettings` to also delete settings).

To only build: `.\build.ps1` (output in `artifacts\publish`).

## Use

The app lives in the system tray. Right-click the icon for:

- **Choose background…** PNG, JPG, BMP or GIF (animated GIFs play at their own speed). Double-clicking the tray icon does the same.
- **Readability wash**: blends the wallpaper toward Claude's background color so text stays easy to read.
- **Blur** and **Fit** (cover or stretch).
- **Animate GIFs** on or off.
- **Calm behind content**: how far the wallpaper fades toward Claude's own background behind text and other detail: Off, Light, Medium (default) or Strong. Empty areas always show the full wallpaper.
- **Wallpaper behind sidebar**: off keeps the sidebar solid.
- **Renderer**: Automatic uses the GPU; CPU is a compatibility fallback.
- **Pause**, **Start with Windows**, settings folder and log.

On first run the app picks up the wallpaper and look from Codex Backdrop if it is installed.

Settings are stored in `%LOCALAPPDATA%\ClaudeBackdrop\config.json`, the log in `log.txt` next to it.

## How it works

Claude refuses to start with a debugging port unless it holds a token signed by Anthropic, so the stylesheet-injection approach used for other Electron apps is deliberately closed off. Claude Backdrop works entirely from the outside:

1. **Capture.** Windows Graphics Capture delivers every new frame of the Claude window as a GPU texture.
2. **Composite.** A few pixel-shader passes rebuild each frame with the wallpaper behind it. Every Claude pixel is read as ink over Claude's flat background color, using the smallest opacity that reproduces it ("color to alpha"), and the ink is laid over the wallpaper instead: `P + (1 - a) * (W - B)`. Text keeps its exact anti-aliasing; shadows, fade gradients and dialog backdrops darken the wallpaper exactly as they darkened the background, so popups keep their shadows and dialogs still dim everything behind them.

   Nothing is segmented: there is no decision anywhere about what is a panel, a chip or a menu, so there are no region edges to get wrong. Readability comes from a smooth *calm field* instead. At quarter resolution each 4x4 block scores its ink contrast relative to its own backing, a wide Gaussian blur (about 24 px) spreads it, and where the field is high the wallpaper fades toward Claude's background. Text and icons calm the area around them; flat areas score zero whatever their shade, so empty margins show the full wallpaper, soft shadows stay on the wallpaper and a dialog backdrop dims it rather than hiding it. Opaque panels such as menus and the composer have nothing behind them to calm, so they cast no glow. Because the field is heavily blurred it cannot draw a hard edge of its own. The sidebar is detected and kept solid unless blending is turned on. A pane docked on the right that shows none of Claude's background, such as the built-in browser or a preview, is traced to its own edges and kept solid too, so web pages keep their own colors.
3. **Show.** The result goes into a DirectComposition swap chain in a click-through window owned by the Claude window, directly above it. Clicks and typing go straight to Claude.

The overlay always shows a complete, self-consistent copy, so moving content never leaves trails. A captured frame is on screen well under a millisecond of CPU time after it arrives, one display frame behind the real window.

The engine runs on a single thread that owns the overlay window, the window-event hooks, the capture session and the renderer. It handles:

- **Minimize, restore, virtual desktop switches, show from tray:** the copy hides at once and fades back in (160 ms) after Windows' own animation, on a fresh frame.
- **Maximize, restore down, snap, drag and resize:** plain Claude while the window changes, then the copy returns at the new size.
- **Alt-tab:** the copy stays directly above Claude. The wallpaper stops animating while Claude is fully covered and resumes immediately when it comes back.
- **Light and dark themes, new chats, dialogs:** background colors are re-detected four times a second while Claude is changing. The current background stays while it still covers a good part of the window, so a large surface such as the new-chat composer cannot take over, and the sidebar color is read from the left edge only. A dialog backdrop is recognised as an overlay, not as a new background, so the wallpaper dims with it. A new background is only taken for a theme switch when it is a theme gray and Claude's sidebar shows. The readability wash is applied as the wallpaper is drawn, so a change of background takes effect at once. Any real change (theme switch, sidebar shown or hidden) must persist across two checks before it is applied, so a frame caught mid-transition never flashes.
- **Videos, image viewers and other media:** when something that is not Claude's page fills the window (a fullscreen video, a viewer's black backdrop, a document of its own color), the copy fades out and Claude shows untouched, so the wallpaper never shows through black or anything else in the media. It fades back in when Claude's page returns.
- **Sleep, driver updates, lost GPU device, Claude restarting:** everything is rebuilt automatically. Repeated GPU failures fall back to the CPU renderer.

## Performance

Measured on a 1920×1032 window with Claude actively rendering (about 55 frames per second) and an animated GIF wallpaper:

| | CPU (one core) | Frame arrival to screen |
|---|---|---|
| GPU renderer | about 4% | 0.3 ms |
| CPU renderer (fallback) | about 29% | about 12 ms |

When Claude is idle or hidden and the wallpaper is still, the app does no work.

## Limitations

- The Alt-Tab switcher, taskbar previews and Task View show plain Claude: they use Windows' own thumbnail of the Claude window, which the overlay is not part of.
- What you see is one display frame behind the real window.
- Background detection assumes Claude's flat background covers a good part of the window, which holds for Claude's normal layout.
- Inside images in the chat, flat areas that are exactly Claude's background color show the wallpaper.
- Switching to the dark theme while the sidebar is collapsed shows plain Claude until the sidebar is opened (the switch is only trusted when Claude's layout shows).
- A docked pane is kept solid only when none of Claude's background color shows inside it; a web page whose background is exactly that color blends like the chat.
- Highlights drawn in a shade close to Claude's background (a hovered menu item, rows in a dialog) look the same as highlights on the page, so a little wallpaper tints them.
- The CPU renderer (fallback) has no calm field: the wallpaper shows at full strength behind all content.

## Troubleshooting

- The tray menu shows the current state, including which renderer is in use.
- **Open log** shows errors, a once-a-minute performance line, and every change of background, sidebar, docked pane or media state.
- Setting `"Trace": true` in `config.json` logs every visibility transition.
- `ClaudeBackdrop.exe --snapshot out.png [x,y,w,h] [--window 0xHWND] [--key RRGGBB]` renders what the overlay would show for the Claude window (or another window) and writes `out.png`, `out.raw.png` (the window as captured) and `out.calm.png` (the calm field: brighter keeps more of Claude's background). `--key` fixes the background color instead of detecting it, as the engine does while a dialog is open. Only that window is captured.
- `tools\test-harness.ps1` renders `tools\harness.html`, a page built from Claude's design tokens, in each of its states (popup, dialog, new chat, browser pane, image viewer, fullscreen video, dark theme) with headless Chrome, shows each in a frameless window and snapshots it through the compositor. The log line of each snapshot says whether the frame counts as Claude's page, a dialog or media. `tools\test-transitions.ps1` exercises minimize, restore and alt-tab.
- `ClaudeBackdrop.exe --capture-overlay out.png` saves what the running overlay window is currently showing.

## Privacy

The app captures only the Claude window, only to draw it on your screen. Nothing is saved or sent anywhere. The diagnostic snapshot commands write images only when you run them.

## License

MIT. Not affiliated with or endorsed by Anthropic. Claude is a trademark of Anthropic.
