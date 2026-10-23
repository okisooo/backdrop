using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Backdrop;

namespace Backdrop;

// No app startup, windows, capture sessions or desktop interaction. GPU checks use
// small offscreen textures; engine checks use a recording renderer.
static unsafe class EngineChecks
{
    static int checks;

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            CheckDialogs();
            CheckMovement();
            CheckStaleWallpaper();
            CheckWallpaperSettings();
            CheckCaptureFailure();
            CheckGpuCache();
            if (args.Length == 2 && args[0] == "--gif") CheckGif(args[1]);
            Console.WriteLine($"Backdrop engine: {checks} checks passed (no desktop interaction)");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    static BackgroundKeys Keys(int w, int h) => new BackgroundKeys(new[] { 0xFFFFFF }, new Rect(0, 0, w, h));

    static void CheckDialogs()
    {
        const int w = 320, h = 240;
        var px = new uint[w * h];
        foreach (int key in new[] { 0xFFFFFF, 0xFCFCFB, 0x303030 })
        {
            Array.Fill(px, 0xFF000000u | (uint)key);
            var keys = Keys(w, h);
            // Use each app's actual theme color while retaining the same area.
            keys.Keys[0] = key;
            fixed (uint* p = px)
                Expect(KeyDetector.Classify((byte*)p, w * 4, w, h, keys, key) == FrameKind.Page, "normal page remains enabled");
            for (int i = 0; i < px.Length; i++)
                px[i] = 0xFF000000u | (uint)((key >> 16) / 2 << 16) | (uint)(((key >> 8) & 255) / 2 << 8) | (uint)((key & 255) / 2);
            for (int y = 50; y < 190; y++) for (int x = 85; x < 255; x++) px[y * w + x] = 0xFF000000u | (uint)key;
            fixed (uint* p = px)
                Expect(KeyDetector.Classify((byte*)p, w * 4, w, h, keys, key) == FrameKind.Dialog, "same-color settings panel must not be keyed");
            // A small hover surface must not be mistaken for broad modal dimming.
            Array.Fill(px, 0xFF000000u | (uint)key);
            for (int y = 90; y < 110; y++) for (int x = 100; x < 200; x++) px[y * w + x] = 0xFF808080;
            fixed (uint* p = px)
                Expect(KeyDetector.Classify((byte*)p, w * 4, w, h, keys, key) == FrameKind.Page, "hover row keeps the wallpaper");
            // A large gray content card is not a modal: its outside margins remain normal.
            for (int y = 30; y < 210; y++) for (int x = 30; x < 290; x++) px[y * w + x] = 0xFF808080;
            fixed (uint* p = px)
                Expect(KeyDetector.Classify((byte*)p, w * 4, w, h, keys, key) == FrameKind.Page, "large content card does not hide the wallpaper");
        }
        Console.WriteLine("  settings/dialog detection: light, off-white, dark; normal/hover controls");
    }

    static Backdrop Engine(RecordingRenderer renderer)
    {
        var engine = (Backdrop)RuntimeHelpers.GetUninitializedObject(typeof(Backdrop));
        Set(engine, "_clock", Stopwatch.StartNew());
        Set(engine, "_nextPoll", long.MaxValue);
        Set(engine, "_renderer", renderer);
        Set(engine, "_settings", new Settings());
        Set(engine, "_haveFrame", true);
        Set(engine, "_keysDirty", true);
        return engine;
    }

    static void CheckMovement()
    {
        var renderer = new RecordingRenderer();
        var engine = Engine(renderer);
        Set(engine, "_inMoveSize", true);
        Invoke(engine, "Step");
        Expect(renderer.Reads == 0, "drag must skip GPU readback and detection");
        Set(engine, "_inMoveSize", false);
        Invoke(engine, "Step");
        Expect(renderer.Reads == 1, "detection must resume after drag");

        Set(engine, "_wantVisible", true);
        Set(engine, "_showRequestedAt", -1L);
        Set(engine, "_returningFromMove", true);
        Set(engine, "_frameArrived", Stopwatch.GetTimestamp());
        Invoke(engine, "UpdateVisibility", 10L); // No prepared wallpaper, so no Show() or native window calls.
        Expect(Get<bool>(engine, "_freshSinceRequest"), "post-drag frame is accepted on this pass");
        Expect(Get<int>(engine, "_fadeDuration") <= 50, "post-drag reveal must use a short fade");
        Expect(!Get<bool>(engine, "_returningFromMove"), "move transition is consumed once");

        Set(engine, "_showRequestedAt", -1L);
        Set(engine, "_returningFromMove", false);
        Invoke(engine, "UpdateVisibility", 20L);
        Expect(!Get<bool>(engine, "_freshSinceRequest"), "ordinary restore still waits for fresh content");
        Expect(Get<int>(engine, "_fadeDuration") == 160, "ordinary restore animation stays intact");
        Console.WriteLine("  movement: processing pauses/resumes; fresh drag frame and short reveal");
    }

    static void CheckWallpaperSettings()
    {
        string firstPath = Path.Combine(Path.GetTempPath(), $"backdrop-check-{Guid.NewGuid():N}.png");
        string secondPath = Path.Combine(Path.GetTempPath(), $"backdrop-check-{Guid.NewGuid():N}.png");
        var first = Engine(new RecordingRenderer());
        var second = Engine(new RecordingRenderer());
        Wallpaper replacement = null;
        try
        {
            using (var image = new System.Drawing.Bitmap(4, 4))
            {
                image.Save(firstPath, System.Drawing.Imaging.ImageFormat.Png);
                image.Save(secondPath, System.Drawing.Imaging.ImageFormat.Png);
            }
            var settings = new Settings { BackgroundPath = firstPath };
            Invoke(first, "ApplySettings", settings);
            Invoke(second, "ApplySettings", settings);
            var shared = Get<Wallpaper>(first, "_wall");
            Expect(ReferenceEquals(shared, Get<Wallpaper>(second, "_wall")), "engines share decoded wallpaper frames");
            replacement = Wallpaper.AcquireShared(secondPath);
            Invoke(first, "ApplySettings", new Settings { BackgroundPath = firstPath, Enabled = false });
            Expect(Get<Wallpaper>(first, "_wall") == null, "paused engine releases its wallpaper");
            Expect(shared.FrameCount == 1, "another engine keeps the old wallpaper alive when the cache changes");
            Expect(replacement.FrameCount == 1, "releasing an old wallpaper leaves the replacement alive");
            var prepared = shared.Prepare(16, 16, "fill", 0);
            Set(second, "_prepared", prepared);
            Set(second, "_shownPrepared", prepared);
            Invoke(second, "ApplySettings", new Settings { BackgroundPath = secondPath + ".missing" });
            Expect(Get<PreparedWallpaper>(second, "_prepared") == null && Get<PreparedWallpaper>(second, "_shownPrepared") == null,
                "invalid wallpaper clears the previous prepared frames");
            Expect(shared.FrameCount == 0 && replacement.FrameCount == 1, "each wallpaper has its own reference lifetime");
        }
        finally
        {
            Get<Wallpaper>(first, "_wall")?.Dispose();
            Get<Wallpaper>(second, "_wall")?.Dispose();
            replacement?.Dispose();
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
        Console.WriteLine("  shared wallpaper: engine integration, pause, replacement lifetime, invalid-path clearing");
    }

    static void CheckCaptureFailure()
    {
        var renderer = new RecordingRenderer();
        var engine = Engine(renderer);
        var capture = (WindowCapture)RuntimeHelpers.GetUninitializedObject(typeof(WindowCapture));
        int wakes = 0;
        Set(capture, "_gate", new object());
        Set(capture, "_onChange", (Action)(() => wakes++));
        // A failed/disposed frame pool must not throw out of its callback thread.
        Invoke(capture, "OnFrameArrived", null, null);
        var failure = capture.Failure;
        Expect(failure != null && wakes == 1, "capture callback contains its fault and wakes recovery");
        Set(capture, "_disposed", true);
        Invoke(capture, "OnFrameArrived", null, null);
        Expect(wakes == 1, "late callbacks after disposal do not request recovery");
        Set(engine, "_capture", capture);
        try { Invoke(engine, "Step"); throw new Exception("capture failure was swallowed"); }
        catch (TargetInvocationException ex)
        {
            Expect(ex.InnerException is InvalidOperationException && ReferenceEquals(ex.InnerException.InnerException, failure),
                "capture callback fault reaches engine recovery");
        }
        Expect(renderer.Reads == 0, "failed capture is not rendered/read back");
        Console.WriteLine("  capture failure: device-loss handoff before any frame processing");
    }

    static void CheckStaleWallpaper()
    {
        var engine = Engine(new RecordingRenderer());
        var settings = Get<Settings>(engine, "_settings");
        var wall = (Wallpaper)RuntimeHelpers.GetUninitializedObject(typeof(Wallpaper));
        var valid = new PreparedWallpaper(wall, new[] { new uint[] { 0xFFFF0000 } }, 1, 1, 64, 64, settings.Fit, settings.BlurPx);
        var stale = valid with { Width = 128 };
        Set(engine, "_wall", wall);
        Set(engine, "_prepared", valid);
        Set(engine, "_shownPrepared", valid);
        Set(engine, "_shownFrame", 0);
        Set(engine, "_prepTask", Task.FromResult(stale));
        Invoke(engine, "UpdateWallpaper", 0L);
        Expect(ReferenceEquals(Get<PreparedWallpaper>(engine, "_prepared"), valid), "stale resize preparation must not replace the current wallpaper");
        Expect(Get<Task<PreparedWallpaper>>(engine, "_prepTask") == null, "matching wallpaper must not trigger another preparation job");
        Console.WriteLine("  wallpaper preparation: stale resize results discarded without extra jobs");
    }

    static void CheckGpuCache()
    {
        const int w = 64, h = 64;
        using var gpu = new GpuDevice();
        using var renderer = new GpuRenderer(gpu, offscreen: true);
        Expect(renderer.Window == null, "offscreen diagnostics create no native window");
        IntPtr texture = gpu.CreateTexture(w, h, GpuDevice.UsageDefault, GpuDevice.BindShaderResource);
        try
        {
            var px = new uint[w * h]; Array.Fill(px, 0xFFFFFFFF);
            for (int y = 28; y < 36; y++) for (int x = 28; x < 36; x++) px[y * w + x] = 0xFF000000;
            fixed (uint* p = px) gpu.Upload(texture, p, w * 4);
            renderer.SetCapture(texture, w, h);
            renderer.SetKeys(Keys(w, h), 1);
            renderer.SetCalm(0.5); renderer.SetWash(0.25);
            renderer.SetWallpaper(new uint[] { 0xFFFF0000 }, 1, 1);
            var red = Read(renderer, w, h);
            Expect(renderer.CalmBuildCount == 1, "first content builds the density/blur");
            renderer.SetWallpaper(new uint[] { 0xFF0000FF }, 1, 1);
            var blue = Read(renderer, w, h);
            Expect(!red.AsSpan().SequenceEqual(blue), "animated wallpaper changes visible pixels");
            Expect(renderer.CalmBuildCount == 1, "wallpaper-only frame reuses all blur passes");
            Expect(blue[32 * w + 32] == red[32 * w + 32], "ink stays unchanged across animated wallpaper frames");
            renderer.SetCalm(0.75); renderer.SetWash(0.55);
            Read(renderer, w, h);
            Expect(renderer.CalmBuildCount == 1, "strength/wash changes do not rebuild content density");
            renderer.SetKeys(Keys(w, h), 2); Read(renderer, w, h);
            Expect(renderer.CalmBuildCount == 2, "changed key/tolerance invalidates cached density");
            px[0] = 0xFF404040;
            fixed (uint* p = px) gpu.Upload(texture, p, w * 4);
            renderer.SetCapture(texture, w, h); Read(renderer, w, h);
            Expect(renderer.CalmBuildCount == 3, "new app frame invalidates cached density");
            renderer.SetCalm(0);
            renderer.SetCapture(texture, w, h); Read(renderer, w, h);
            Expect(renderer.CalmBuildCount == 3, "disabled calm skips all density/blur passes");
            renderer.SetCalm(0.5); Read(renderer, w, h);
            Expect(renderer.CalmBuildCount == 4, "re-enabled calm consumes the pending app frame");
            renderer.SetCapture(texture, 32, 48);
            renderer.SetKeys(Keys(32, 48), 1);
            var resized = Read(renderer, 32, 48);
            Expect(resized.Length == 32 * 48 && renderer.CalmBuildCount == 5, "resize rebuilds correctly sized density textures");
            Console.WriteLine("  GPU pixels/cache: animation preserved; GIF-only frames skip 3 passes");
        }
        finally { if (texture != IntPtr.Zero) System.Runtime.InteropServices.Marshal.Release(texture); }
    }

    static uint[] Read(GpuRenderer renderer, int w, int h)
    {
        var result = new uint[w * h];
        renderer.RenderOffscreen(1, false, (rows, pitch, rw, rh) =>
        {
            fixed (uint* dst = result)
                for (int y = 0; y < h; y++) Buffer.MemoryCopy(rows + y * pitch, dst + y * w, w * 4, w * 4);
        });
        return result;
    }

    static void CheckGif(string path)
    {
        using var wall = Wallpaper.Load(path);
        Expect(wall.FrameCount > 1, "custom GIF retains all animation frames");
        var prep = wall.Prepare(48, 48, "fill", 19);
        Expect(prep.Frames.Length == wall.FrameCount, "preparation does not turn animation into a still");
        Expect(wall.Delay(0) >= 20, "GIF frame delay remains valid");
        Console.WriteLine($"  custom GIF: {wall.FrameCount} frames retained at original delays");
    }

    static void Expect(bool valid, string message) { if (!valid) throw new Exception(message); checks++; }
    static FieldInfo Field(object obj, string name) => obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    static void Set(object obj, string name, object value) => Field(obj, name).SetValue(obj, value);
    static T Get<T>(object obj, string name) => (T)Field(obj, name).GetValue(obj);
    static object Invoke(object obj, string name, params object[] args) => obj.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(obj, args);

    sealed class RecordingRenderer : IRenderer
    {
        public int Reads;
        public string Name => "test";
        public OverlayWindow Window => null;
        public int Width => 64;
        public int Height => 64;
        public void ReadCapture(GpuDevice.RowsReader reader) => Reads++;
        public void SetCapture(IntPtr texture, int w, int h) => throw new Exception("unexpected capture copy");
        public void SetKeys(BackgroundKeys keys, int tolerance) { }
        public void SetCalm(double strength) { }
        public void SetWash(double amount) { }
        public void SetWallpaper(uint[] pixels, int sw, int sh) => throw new Exception("unexpected wallpaper processing");
        public void Render(float opacity, int x, int y) => throw new Exception("unexpected desktop rendering");
        public void Move(int x, int y) => throw new Exception("unexpected window movement");
        public void Dispose() { }
    }
}
