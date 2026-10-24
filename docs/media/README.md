# Demo assets

## Real app recording

`real-app-demo.gif` is a 10-second, looping recording of **Claude Desktop** with Backdrop running. `real-app-demo.mp4` is the higher-quality video; `real-app-demo.png` is a still.

`backdrop-share.png` is the custom GitHub link-preview image, built from this same real recording.

- **Original app**, on the left, is a still captured directly from Claude's window before entering the sample prompt.
- **Backdrop enabled**, on the right, is footage captured directly from the running Backdrop overlay. The wallpaper animation and composer are real app output.
- The app was opened to an empty new chat with its sidebar hidden. The only entered text is the unsent sample prompt, "Help me plan a calm, focused workspace."
- Only the reviewed interior of that window was saved: a 724 x 796 crop from a 756 x 902 capture. Window controls, sidebar, account details, environment controls, and the desktop are outside the crop. Recording stops on a size mismatch.
- All 300 source frames were checked with English text recognition, and a contact sheet was reviewed before publication. Recognized content consists of Claude's generic greeting, its composer placeholder, and the sample prompt. No private conversations are included.

Capture uses the same Windows.Graphics.Capture implementation as Backdrop. The two captures were scaled and placed beside one another with labels in FFmpeg; no application interface was recreated. The final GIF is 960 x 711 at 10 fps; the MP4 is 1080 x 800 at 15 fps.

The recording demonstrates Claude Desktop. Support for Codex Desktop and T3 Code is documented in the [main README](../../README.md); they are not shown in this recording.

## Earlier renderer samples

The following older assets contain **invented sample interfaces**, rather than recordings of Claude, Codex, or T3 Code. They demonstrate the production `KeyDetector` and `GpuRenderer` with each app's rendering profile. Their text and procedural wallpaper are original, with no user chats or account details:

- `overview.gif`: 9-second loop covering the three rendering profiles.
- `readability.gif`: plain and composited versions of sample content.
- `backdrop-demo.mp4`: video of the renderer samples.
- `social-preview.png` and `demo-still.png`: sample artwork.
- `sample-wallpaper.gif`: original procedural wallpaper included in the portable release.

The renderer sample generator is in [`tools/Backdrop.Demo`](../../tools/Backdrop.Demo). It creates no windows, capture sessions, app settings, or network requests. To reproduce those earlier samples on Windows with the .NET 9 SDK, Direct3D 11, and FFmpeg:

```powershell
.\tools\render-demo.ps1 -FramesDirectory .\artifacts\demo-frames -OutputDirectory .\docs\media
```

The sample generator and its original artwork are covered by the repository's MIT license. The real recording includes Claude's interface, which belongs to Anthropic; Backdrop is an independent project.
