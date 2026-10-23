using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Backdrop;

static unsafe class Snapshot
{
    public static int Run(string outPath, Rectangle? crop, IntPtr window, int? key = null, TargetApp? requestedApp = null)
    {
        var s = Settings.Load();
        TargetApp app = requestedApp ?? TargetApp.Claude;
        IntPtr target = window;

        if (target != IntPtr.Zero)
        {
            app = requestedApp ?? AppLocator.IdentifyWindow(target) ?? TargetApp.Claude;
        }
        else if (requestedApp is { } explicitApp)
        {
            target = AppLocator.FindMainWindow(explicitApp);
        }
        else
        {
            foreach (var candidate in new[] { TargetApp.T3Code, TargetApp.Claude, TargetApp.Codex })
            {
                target = AppLocator.FindMainWindow(candidate);
                if (target != IntPtr.Zero) { app = candidate; break; }
            }
        }
        if (target == IntPtr.Zero) return 2;

        using var gpu = new GpuDevice();
        var frame = CaptureOne(gpu, target);
        if (frame == null) return 3;

        using var renderer = new GpuRenderer(gpu, offscreen: true);
        int w = frame.ContentSize.Width, h = frame.ContentSize.Height;
        var raw = new uint[w * h];
        IntPtr texture = GpuDevice.TextureOf(frame.Surface);
        try
        {
            renderer.SetCapture(texture, w, h);
            gpu.Read(texture, w, h, (rows, pitch, rw, rh) => CopyRows(rows, pitch, raw, rw, rh));
        }
        finally
        {
            Marshal.Release(texture);
            frame.Dispose();
        }

        BackgroundKeys keys;
        FrameKind kind;
        fixed (uint* p = raw)
        {
            keys = KeyDetector.Detect((byte*)p, w * 4, w, h, s.BlendSidebar, key ?? -1, app) ?? new BackgroundKeys(new[] { 0xFFFFFF }, new Rect(0, 0, w, h));
            kind = KeyDetector.Classify((byte*)p, w * 4, w, h, keys, key ?? -1, app);
            if (kind == FrameKind.Dialog && key is { } k) keys = new BackgroundKeys(new[] { k }, keys.Keyed, keys.Radii, keys.Panel, keys.Pane);
        }
        using var wall = Wallpaper.Load(s.BackgroundPath);
        var prep = wall.Prepare(w, h, s.Fit, s.BlurPx);
        renderer.SetKeys(keys, s.EffectiveTolerance(app));
        renderer.SetCalm(s.Calm);
        renderer.SetWash(s.Wash);
        renderer.SetWallpaper(prep.Frames[0], prep.SmallWidth, prep.SmallHeight);

        var output = new uint[w * h];
        var calm = new uint[w * h];
        if (kind is FrameKind.Media or FrameKind.Dialog) raw.CopyTo(output, 0);
        else renderer.RenderOffscreen(1, false, (rows, pitch, rw, rh) => CopyRows(rows, pitch, output, rw, rh));
        renderer.RenderOffscreen(1, true, (rows, pitch, rw, rh) => CopyRows(rows, pitch, calm, rw, rh));
        Backdrop.Log($"[{AppLocator.DisplayName(app)}] snapshot {w}x{h}: {kind.ToString().ToLowerInvariant()}, {keys}");

        var area = crop ?? new Rectangle(0, 0, w, h);
        area.Intersect(new Rectangle(0, 0, w, h));
        Save(output, w, area, outPath);
        Save(raw, w, area, Path.ChangeExtension(outPath, ".raw.png"));
        Save(calm, w, area, Path.ChangeExtension(outPath, ".calm.png"));
        return 0;
    }

    public static int CaptureOverlay(string outPath, TargetApp? requestedApp = null)
    {
        string expectedTitle = requestedApp is { } a ? $"Backdrop - {AppLocator.DisplayName(a)}" : null;
        IntPtr overlay = IntPtr.Zero;
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h)) return true;
            if ((((long)Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE)) & Native.WS_EX_LAYERED) == 0) return true;
            string title = Native.TitleOf(h);
            bool match = expectedTitle != null
                ? string.Equals(title, expectedTitle, StringComparison.Ordinal)
                : title == "Backdrop" || title.StartsWith("Backdrop - ", StringComparison.Ordinal);
            if (!match) return true;
            overlay = h;
            return false;
        }, IntPtr.Zero);
        if (overlay == IntPtr.Zero) return 2;
        using var gpu = new GpuDevice();
        var frame = CaptureOne(gpu, overlay);
        if (frame == null) return 3;
        int w = frame.ContentSize.Width, h = frame.ContentSize.Height;
        var px = new uint[w * h];
        IntPtr texture = GpuDevice.TextureOf(frame.Surface);
        try { gpu.Read(texture, w, h, (rows, pitch, rw, rh) => CopyRows(rows, pitch, px, rw, rh)); }
        finally
        {
            Marshal.Release(texture);
            frame.Dispose();
        }
        Save(px, w, new Rectangle(0, 0, w, h), outPath);
        return 0;
    }

    static Windows.Graphics.Capture.Direct3D11CaptureFrame CaptureOne(GpuDevice gpu, IntPtr window)
    {
        using var got = new ManualResetEventSlim();
        using var capture = new WindowCapture(window, gpu.WinRTDevice, () => got.Set());
        for (int i = 0; i < 50; i++)
        {
            got.Wait(100);
            var frame = capture.TakeLatest();
            if (frame != null) return frame;
        }
        return null;
    }

    static void CopyRows(byte* rows, int pitch, uint[] dst, int w, int h)
    {
        fixed (uint* d = dst)
            for (int y = 0; y < h; y++) Buffer.MemoryCopy(rows + (long)y * pitch, d + (long)y * w, w * 4, w * 4);
    }

    static void Save(uint[] px, int stride, Rectangle area, string path)
    {
        using var bmp = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, area.Width, area.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        for (int y = 0; y < area.Height; y++)
        {
            uint* dst = (uint*)(data.Scan0 + y * data.Stride);
            for (int x = 0; x < area.Width; x++) dst[x] = px[(area.Y + y) * stride + area.X + x] | 0xFF000000u;
        }
        bmp.UnlockBits(data);
        bmp.Save(path, ImageFormat.Png);
    }
}