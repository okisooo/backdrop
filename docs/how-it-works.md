# How Backdrop works



Modern desktop AI apps either block remote debugging ports or overwrite injected stylesheets on every update. Backdrop works entirely from outside the target process:

1. **Capture.** Windows Graphics Capture delivers each new frame of an attached window as a Direct3D 11 texture.
2. **Detect & Composite.** Pixel-shader passes rebuild each frame with your wallpaper behind it. Every app pixel is interpreted as ink over the app's detected background color using minimum color-to-alpha (`P + (1 - a) * (W - B)`). Text keeps its exact subpixel/grayscale anti-aliasing, and shadows or fade gradients darken the wallpaper naturally.
   - **Calm field:** At quarter resolution, each 4×4 block scores local ink contrast and spreads it with a separable Gaussian blur (~24 px). Behind dense text and UI controls, the wallpaper calms toward the app's background color; in open margins, the full wallpaper shows through.
   - **App-aware layout detection:**
     - **Codex:** Traces the rounded main content panel (`FindPanel` + corner radius measurement) so the outer Mica window frame and translucent sidebar stay untouched unless sidebar blending is enabled.
     - **Claude:** Detects the left sidebar boundary and any right-docked preview/browser pane so web previews keep their own colors.
     - **T3 Code:** Uses neighborhood-aggregated color clustering and a minimum per-channel tolerance of `4` so custom tinted themes (like `Glacier Cyan`) and T3 Code's 3.5% SVG `--surface-grain` texture key cleanly without noise speckle.
3. **Present.** Each attached app gets a click-through DirectComposition overlay window owned by the target window, sitting directly above it in Z-order. Mouse clicks, scrolling, and keyboard input pass straight through to the app.

When multiple apps are open simultaneously, decoded GIF frames are shared in memory across all active engines while each window prepares its own resolution-matched working frames.

## Performance

Measured on a 1920×1032 window actively rendering at ~55 fps with an animated GIF wallpaper:

| Mode | Engine elapsed work / render | Capture arrival to submission |
|---|---|---|
| GPU renderer | ~0.3 ms | < 1 ms |
| CPU renderer (fallback) | ~12 ms | ~12 ms |

Animation pauses when a target window is hidden, minimized, or fully covered by another window.
