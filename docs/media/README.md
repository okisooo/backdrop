# Demo assets

These demos contain invented sample interfaces, not screenshots of Claude, Codex, or T3 Code. They show the production `KeyDetector` and `GpuRenderer` compositing animated wallpaper behind sample content using each app's rendering profile. The sample text and procedural wallpaper are original; no user chats, account details, or third-party artwork are included.

- `overview.gif`: 9-second loop covering all three rendering profiles.
- `readability.gif`: plain and composited versions of the same sample content.
- `backdrop-demo.mp4`: higher-quality video version for sharing.
- `social-preview.png`: 1280 x 640 share card.
- `demo-still.png`: full-size still.
- `sample-wallpaper.gif`: the original procedural wallpaper, included in the portable release.

The source is in [`tools/Backdrop.Demo`](../../tools/Backdrop.Demo). It creates no windows, capture sessions, app settings, or network requests. Reproduce the media on Windows with the .NET 9 SDK, Direct3D 11, and FFmpeg:

```powershell
.\tools\render-demo.ps1 -FramesDirectory .\artifacts\demo-frames -OutputDirectory .\docs\media
```

The GIFs are encoded at 15 fps. Assets and their generator are covered by the repository's MIT license.
