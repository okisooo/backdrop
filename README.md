# Backdrop

A lightweight Windows GPU tray app that puts custom images or animated GIFs behind **Claude Desktop**, **OpenAI Codex**, and **T3 Code** simultaneously—without modifying any app files, injecting scripts, or restarting your apps.

## Highlights

- **One tray app for all 3 coding assistants**: Hooks **Claude**, **Codex**, and **T3 Code** automatically from a single system tray icon, with per-app toggles (`Claude`, `Codex`, `T3 Code`).
- **Zero app modification**: Uses Windows Graphics Capture + a Direct3D 11 / DirectComposition shader overlay (`color-to-alpha` keying). Survives every app auto-update.
- **Content-aware `Calm` halo**: Measures ink density at quarter resolution and blurs a 24px Gaussian field behind text, code blocks, and UI chrome so text stays effortless to read while empty chat areas show the full animated wallpaper.
- **App-aware layout detection**:
  - **Codex**: Detects the rounded main panel inside Codex's Mica window frame and preserves docked browser/preview panes.
  - **Claude**: Preserves the left sidebar and right artifact/preview panes.
  - **T3 Code**: Supports both neutral and tinted custom themes (such as `Glacier Cyan`) and bucket-filters T3 Code's SVG surface grain cleanly.

## Install

Requirements: Windows 10 2004+ (Windows 11 recommended) and the [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0).

```powershell
.\install.ps1
```

This builds `Backdrop.exe`, stops and migrates any previous standalone `ClaudeBackdrop` / `CodexBackdrop` / `T3Backdrop` installs, installs to `%LOCALAPPDATA%\Programs\Backdrop\Backdrop.exe`, adds a **Backdrop** Start menu shortcut, and launches it in the system tray.

## Settings

Stored in `%LOCALAPPDATA%\Backdrop\config.json` (automatically imported from `CodexBackdrop` or `ClaudeBackdrop` on first run).