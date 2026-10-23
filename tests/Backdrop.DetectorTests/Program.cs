using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;

namespace Backdrop;

static unsafe class Program
{
    const int W = 1400, H = 1100;
    const int SidebarX = 50, DividerX = 380, Top = 44, Right = W - 4, Bottom = H - 4;
    const int RadiusTopRight = 12, RadiusBottomRight = 4;

    static int _failures;

    static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            foreach (string path in args) Describe(path);
            return 0;
        }

        var light = Frame(0xFFFFFF, Mica, LightSidebar, 0xDDE4E8, 0xF4F4F4, 0x1F1F1F);
        var dark = Frame(0x212121, y => Mix(0x2B2B2B, 0x2F2E33, y / (double)H), y => Mix(0x1C1C1C, 0x1E1D22, y / (double)H), 0x333333, 0x303030, 0xECECEC);

        Detect("codex light, solid sidebar", light, blend: false, TargetApp.Codex, out var keys);
        Expect("light key", keys.Keys[0], 0xFFFFFF);
        ExpectNear("panel left", keys.Keyed.X0, DividerX + 1, 1);
        ExpectNear("panel top", keys.Keyed.Y0, Top, 1);
        ExpectNear("panel right", keys.Keyed.X1, Right, 1);
        ExpectNear("panel bottom", keys.Keyed.Y1, Bottom, 1);
        Expect("square top-left corner", keys.Radii.TopLeft, 0);
        ExpectNear("rounded top-right corner", keys.Radii.TopRight, RadiusTopRight, 2);
        ExpectNear("rounded bottom-right corner", keys.Radii.BottomRight, RadiusBottomRight, 2);
        Expect("square bottom-left corner", keys.Radii.BottomLeft, 0);
        Expect("sidebar shown as drawn", keys.Contains(SidebarX + 100, 500), false);
        Expect("title bar shown as drawn", keys.Contains(800, Top / 2), false);
        Expect("chat keyed", keys.Contains(800, 500), true);
        Expect("frame outside the rounded corner shown as drawn", keys.Contains(Right - 1, Top), false);
        Expect("panel just inside the rounded corner keyed", keys.Contains(Right - RadiusTopRight, Top + RadiusTopRight), true);
        Expect("light frame is the page", Classify(light, keys, -1, TargetApp.Codex), FrameKind.Page);

        Detect("codex light, wallpaper behind sidebar", light, blend: true, TargetApp.Codex, out var blended);
        Expect("whole window keyed", blended.Keyed, new Rect(0, 0, W, H));
        Expect("panel still recognized", blended.Panel, true);
        Expect("sidebar keyed", blended.Contains(SidebarX + 100, 500), true);

        Detect("codex dark, solid sidebar", dark, blend: false, TargetApp.Codex, out var darkKeys);
        Expect("dark key", darkKeys.Keys[0], 0x212121);
        ExpectNear("dark panel left", darkKeys.Keyed.X0, DividerX + 1, 1);
        Expect("dark frame is the page", Classify(dark, darkKeys, -1, TargetApp.Codex), FrameKind.Page);

        var dialog = Dim(light, 0.5);
        Detect("dialog backdrop", dialog, blend: false, TargetApp.Codex, out var dialogKeys, current: 0xFFFFFF);
        Expect("dimmed page is a dialog", Classify(dialog, dialogKeys, 0xFFFFFF, TargetApp.Codex), FrameKind.Dialog);

        var settingsDialog = Dim(light, 0.5);
        FillRect(settingsDialog, 420, 180, 1280, 920, 0xFFFFFF);
        Detect("settings panel over dimmed page", settingsDialog, blend: false, TargetApp.Codex, out var settingsKeys, current: 0xFFFFFF);
        Expect("same-color settings panel is protected", Classify(settingsDialog, settingsKeys, 0xFFFFFF, TargetApp.Codex), FrameKind.Dialog);

        var media = Media();
        Detect("video viewer", media, blend: false, TargetApp.Codex, out var mediaKeys, current: 0xFFFFFF);
        Expect("black viewer is media", Classify(media, mediaKeys, 0xFFFFFF, TargetApp.Codex), FrameKind.Media);
        Expect("theme switch to dark is the page", Classify(dark, darkKeys, 0xFFFFFF, TargetApp.Codex), FrameKind.Page);

        var noPanel = Fill(0xFFFFFF);
        Detect("flat white, no layout", noPanel, blend: false, TargetApp.Codex, out var flat);
        Expect("no panel found", flat.Panel, false);
        Expect("falls back to the whole window", flat.Keyed, new Rect(0, 0, W, H));

        CheckClaudeAndT3();
        CheckMigration();
        CheckLocatorConcurrency();

        Console.WriteLine(_failures == 0 ? "detector: all checks passed" : $"detector: {_failures} check(s) failed");
        return _failures == 0 ? 0 : 1;
    }

    static void CheckClaudeAndT3()
    {
        var claude = Fill(0xFAF9F5);
        FillRect(claude, 0, 0, 320, H, 0xF0EEE6);
        Detect("claude light with sidebar", claude, blend: false, TargetApp.Claude, out var claudeKeys);
        Expect("claude key", claudeKeys.Keys[0], 0xFAF9F5);
        ExpectNear("claude sidebar width", claudeKeys.Keyed.X0, 320, 2);
        Expect("claude page classified", Classify(claude, claudeKeys, -1, TargetApp.Claude), FrameKind.Page);

        var t3 = Fill(0xE4F0F5);
        FillRect(t3, 0, 0, 378, H, 0xD6E6EC);
        Detect("t3 glacier-cyan with sidebar", t3, blend: false, TargetApp.T3Code, out var t3Keys);
        Expect("t3 key", t3Keys.Keys[0], 0xE4F0F5);
        ExpectNear("t3 sidebar width", t3Keys.Keyed.X0, 378, 2);
        Expect("t3 page classified", Classify(t3, t3Keys, -1, TargetApp.T3Code), FrameKind.Page);

        const int gw = 320, gh = 240;
        var grain = new uint[gw * gh];
        for (int y = 0; y < gh; y++)
        for (int x = 0; x < gw; x++)
        {
            int r = 199 + ((x / 5) % 2);
            int g = 199 + ((y / 5) % 2);
            int b = 199 + (((x / 5 + y / 5) / 2) % 2);
            grain[y * gw + x] = 0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | (uint)b;
        }
        BackgroundKeys? grainKeys;
        fixed (uint* p = grain) grainKeys = KeyDetector.Detect((byte*)p, gw * 4, gw, gh, false, -1, TargetApp.T3Code);
        Expect("t3 boundary grain (199/200) detected", grainKeys != null, true);
    }

    static int Mica(int y) => Mix(0xEEF4F9, 0xEFF9DF, y / (double)H);
    static int LightSidebar(int y) => Mix(0xF9FBFD, 0xF9FDF4, y / (double)H);

    static uint[] Frame(int background, Func<int, int> frame, Func<int, int> sidebar, int divider, int bubble, int ink)
    {
        var px = new uint[W * H];
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            int c = frame(y);
            if (y >= Top && y < Bottom && x >= SidebarX && x < DividerX) c = sidebar(y);
            else if (y >= Top && y < Bottom && x == DividerX) c = divider;
            else if (y >= Top && y < Bottom && x > DividerX && x < Right && InsidePanel(x, y)) c = background;
            px[y * W + x] = 0xFF000000u | (uint)c;
        }
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8 - y; x++) px[y * W + x] = px[y * W + W - 1 - x] = 0;
        for (int i = 0; i < 6; i++)
        {
            int y0 = 120 + i * 150;
            bool mine = i % 2 == 1;
            int x0 = mine ? 900 : 520, x1 = mine ? 1300 : 1150;
            FillRect(px, x0, y0, x1, y0 + 110, bubble);
            for (int line = 0; line < 4; line++) FillRect(px, x0 + 20, y0 + 18 + line * 22, x1 - 40 - line * 37, y0 + 30 + line * 22, ink);
        }
        FillRect(px, 560, 1010, 1240, 1012, 0xE3E3E3);
        FillRect(px, Right - 10, 300, Right - 4, 620, 0xEDEEEE);
        for (int i = 0; i < 20; i++) FillRect(px, SidebarX + 20, 100 + i * 45, SidebarX + 220, 112 + i * 45, ink);
        for (int i = 0; i < 4; i++) FillRect(px, 16, 60 + i * 48, 34, 78 + i * 48, ink);
        return px;
    }

    static bool InsidePanel(int x, int y)
    {
        static bool Arc(int x, int y, int cx, int cy, int r) => (x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - cy) * (y + 0.5 - cy) <= r * r;
        if (x >= Right - RadiusTopRight && y < Top + RadiusTopRight) return Arc(x, y, Right - RadiusTopRight, Top + RadiusTopRight, RadiusTopRight);
        if (x >= Right - RadiusBottomRight && y >= Bottom - RadiusBottomRight) return Arc(x, y, Right - RadiusBottomRight, Bottom - RadiusBottomRight, RadiusBottomRight);
        return true;
    }

    static uint[] Dim(uint[] src, double factor)
    {
        var px = new uint[src.Length];
        for (int i = 0; i < src.Length; i++)
        {
            uint p = src[i];
            if (p >> 24 != 255) { px[i] = p; continue; }
            uint r = (uint)(((p >> 16) & 255) * factor), g = (uint)(((p >> 8) & 255) * factor), b = (uint)((p & 255) * factor);
            px[i] = 0xFF000000u | (r << 16) | (g << 8) | b;
        }
        return px;
    }

    static uint[] Media()
    {
        var px = Fill(0x000000);
        FillRect(px, 300, 200, 1100, 900, 0x3A6EA5);
        return px;
    }

    static uint[] Fill(int color)
    {
        var px = new uint[W * H];
        Array.Fill(px, 0xFF000000u | (uint)color);
        return px;
    }

    static void FillRect(uint[] px, int x0, int y0, int x1, int y1, int color)
    {
        for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) px[y * W + x] = 0xFF000000u | (uint)color;
    }

    static int Mix(int a, int b, double t)
    {
        int Ch(int shift) => (int)Math.Round(((a >> shift) & 255) * (1 - t) + ((b >> shift) & 255) * t);
        return (Ch(16) << 16) | (Ch(8) << 8) | Ch(0);
    }

    static void Detect(string label, uint[] px, bool blend, TargetApp app, out BackgroundKeys keys, int current = -1)
    {
        BackgroundKeys? found;
        fixed (uint* p = px) found = KeyDetector.Detect((byte*)p, W * 4, W, H, blend, current, app);
        keys = found ?? default;
        Console.WriteLine($"  {label}: {(found is { } k ? k.ToString() : "nothing found")}");
        if (found == null) Fail($"{label}: no background found");
    }

    static FrameKind Classify(uint[] px, BackgroundKeys keys, int current, TargetApp app)
    {
        fixed (uint* p = px) return KeyDetector.Classify((byte*)p, W * 4, W, H, keys, current, app);
    }

    static void Describe(string path)
    {
        using var bmp = new Bitmap(path);
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var found = KeyDetector.Detect((byte*)data.Scan0, data.Stride, bmp.Width, bmp.Height, false, -1, TargetApp.Codex);
            string kind = found is { } k ? KeyDetector.Classify((byte*)data.Scan0, data.Stride, bmp.Width, bmp.Height, k, -1, TargetApp.Codex).ToString() : "-";
            Console.WriteLine($"{Path.GetFileName(path)}: {kind}, {(found is { } f ? f.ToString() : "nothing found")}");
        }
        finally { bmp.UnlockBits(data); }
    }

    static void CheckMigration()
    {
        using var v1 = JsonDocument.Parse("""
            { "schemaVersion": 8, "backgroundPath": "D:\\pics\\loop.gif", "canvasOpacity": 0.712, "surfaceOpacity": 0.326,
              "blurPx": 18.82, "mediaFit": "fill", "animate": false, "sidebarStyle": "solid", "keepAttached": true }
            """);
        var s = Settings.FromVersion1(v1.RootElement);
        Expect("background kept", s.BackgroundPath, @"D:\pics\loop.gif");
        Expect("wash snapped to a menu preset", s.Wash, 0.75);
        Expect("calm snapped to a menu preset", s.Calm, 0.5);
        Expect("blur snapped to a menu preset", s.BlurPx, 19.0);
        Expect("fit", s.Fit, "fill");
        Expect("animate", s.Animate, false);
        Expect("solid sidebar", s.BlendSidebar, false);
        Expect("enabled", s.Enabled, true);

        using var video = JsonDocument.Parse("""{ "schemaVersion": 7, "backgroundPath": "D:\\clips\\rain.mp4", "sidebarStyle": "blended", "keepAttached": false }""");
        var v = Settings.FromVersion1(video.RootElement);
        Expect("video dropped (images and GIFs only)", v.BackgroundPath, "");
        Expect("blended sidebar", v.BlendSidebar, true);
        Expect("paused stays paused", v.Enabled, false);

        string tempDir = Path.Combine(Path.GetTempPath(), "BackdropMigrationTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            string codexDir = Path.Combine(tempDir, "CodexBackdrop");
            Directory.CreateDirectory(codexDir);
            string img = Path.Combine(tempDir, "wall.png");
            File.WriteAllBytes(img, [1]);
            File.WriteAllText(Path.Combine(codexDir, "config.json"), JsonSerializer.Serialize(new
            {
                BackgroundPath = img,
                Wash = 0.75,
                Enabled = false,
                Renderer = "cpu",
                KeyTolerance = 7,
                Trace = true
            }));
            var imported = new Settings();
            imported.ImportFromLegacy(tempDir);
            Expect("legacy v2 background kept", imported.BackgroundPath, img);
            Expect("legacy v2 enabled kept", imported.Enabled, false);
            Expect("legacy v2 renderer kept", imported.Renderer, "cpu");
            Expect("legacy v2 tolerance kept", imported.KeyTolerance, 7);
            Expect("legacy v2 trace kept", imported.Trace, true);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    static void CheckLocatorConcurrency()
    {
        var errors = new ConcurrentQueue<Exception>();
        var tasks = Enumerable.Range(0, 3).Select(t => Task.Run(() =>
        {
            for (uint i = 0; i < 1500; i++)
            {
                try { AppLocator.IsTargetProcess(10000000 + i * 3 + (uint)t, (TargetApp)t); }
                catch (Exception ex) { errors.Enqueue(ex); break; }
            }
        })).ToArray();
        Task.WaitAll(tasks, 15000);
        Expect("parallel AppLocator cache thread safety", errors.Count, 0);
    }

    static void Expect<T>(string label, T actual, T expected)
    {
        if (EqualityComparer<T>.Default.Equals(actual, expected)) return;
        Fail($"{label}: expected {expected}, got {actual}");
    }

    static void ExpectNear(string label, int actual, int expected, int tolerance)
    {
        if (Math.Abs(actual - expected) <= tolerance) return;
        Fail($"{label}: expected {expected} (±{tolerance}), got {actual}");
    }

    static void Fail(string message)
    {
        _failures++;
        Console.WriteLine("FAIL " + message);
    }
}