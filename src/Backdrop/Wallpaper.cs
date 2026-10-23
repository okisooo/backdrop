using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Backdrop;

sealed record PreparedWallpaper(Wallpaper Source, uint[][] Frames, int SmallWidth, int SmallHeight,
    int Width, int Height, string Fit, double BlurPx);

sealed class Wallpaper : IDisposable
{
    static readonly object CacheSync = new();
    static string _cachedPath;
    static DateTime _cachedWriteUtc;
    static Wallpaper _cachedInstance;

    readonly object _sourceLock = new();
    readonly List<Bitmap> _source = new();
    readonly List<int> _delays = new();
    bool _shared;
    int _references;

    public int FrameCount => _source.Count;
    public int Delay(int frame) => _delays[frame];

    public static Wallpaper Load(string path)
    {
        var wp = new Wallpaper();
        using var stream = new MemoryStream(File.ReadAllBytes(path));
        using var img = Image.FromStream(stream);
        int count = 1;
        try { count = img.GetFrameCount(FrameDimension.Time); } catch { }
        byte[] delays = null;
        try { delays = img.GetPropertyItem(0x5100)?.Value; } catch { }
        for (int i = 0; i < count; i++)
        {
            if (count > 1) img.SelectActiveFrame(FrameDimension.Time, i);
            var frame = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(frame)) g.DrawImage(img, 0, 0, img.Width, img.Height);
            wp._source.Add(frame);
            int d = delays != null && delays.Length >= (i + 1) * 4 ? BitConverter.ToInt32(delays, i * 4) * 10 : 100;
            wp._delays.Add(d < 20 ? 100 : d);
        }
        return wp;
    }

    public static Wallpaper AcquireShared(string path)
    {
        string full = Path.GetFullPath(path);
        DateTime writeUtc = File.GetLastWriteTimeUtc(full);
        lock (CacheSync)
        {
            if (_cachedInstance != null && string.Equals(_cachedPath, full, StringComparison.OrdinalIgnoreCase) && _cachedWriteUtc == writeUtc)
            {
                _cachedInstance._references++;
                return _cachedInstance;
            }
            var loaded = Load(full);
            loaded._shared = true;
            loaded._references = 1;
            _cachedPath = full;
            _cachedWriteUtc = writeUtc;
            _cachedInstance = loaded;
            return loaded;
        }
    }

    // Thread-safe: locks each source Bitmap only while scaling into the destination frame.
    public PreparedWallpaper Prepare(int w, int h, string fit, double blurPx)
    {
        int scale = Math.Max(1, (int)Math.Ceiling(Math.Max(w, h) / 480.0));
        int sw = Math.Max(1, w / scale), sh = Math.Max(1, h / scale);
        int radius = (int)Math.Round(blurPx / scale);
        var frames = new uint[_source.Count][];
        for (int i = 0; i < _source.Count; i++)
        {
            var px = GC.AllocateArray<uint>(sw * sh, pinned: true);
            using (var dst = new Bitmap(sw, sh, sw * 4, PixelFormat.Format32bppPArgb, Marshal.UnsafeAddrOfPinnedArrayElement(px, 0)))
            using (var g = Graphics.FromImage(dst))
            using (var attrs = new ImageAttributes())
            {
                attrs.SetWrapMode(WrapMode.TileFlipXY);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingMode = CompositingMode.SourceCopy;
                lock (_sourceLock)
                {
                    var src = _source[i];
                    RectangleF from = fit == "fill" ? new RectangleF(0, 0, src.Width, src.Height) : CoverRect(src.Width, src.Height, sw, sh);
                    g.DrawImage(src, new Rectangle(0, 0, sw, sh), from.X, from.Y, from.Width, from.Height, GraphicsUnit.Pixel, attrs);
                }
            }
            if (radius > 0) BoxBlur(px, sw, sh, radius, 3);
            frames[i] = px;
        }
        return new PreparedWallpaper(this, frames, sw, sh, w, h, fit, blurPx);
    }

    static RectangleF CoverRect(int iw, int ih, int w, int h)
    {
        float s = Math.Max((float)w / iw, (float)h / ih);
        float cw = w / s, ch = h / s;
        return new RectangleF((iw - cw) / 2, (ih - ch) / 2, cw, ch);
    }

    public static void Wash(uint[] src, uint[] dst, uint color, double amount)
    {
        int a = (int)Math.Round(Math.Clamp(amount, 0, 1) * 256);
        int ia = 256 - a;
        int cr = (int)(color >> 16) & 255, cg = (int)(color >> 8) & 255, cb = (int)color & 255;
        for (int i = 0; i < src.Length; i++)
        {
            uint p = src[i];
            int r = (((int)(p >> 16) & 255) * ia + cr * a) >> 8;
            int g = (((int)(p >> 8) & 255) * ia + cg * a) >> 8;
            int b = (((int)p & 255) * ia + cb * a) >> 8;
            dst[i] = 0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | (uint)b;
        }
    }

    static void BoxBlur(uint[] px, int w, int h, int r, int passes)
    {
        var tmp = new uint[px.Length];
        for (int p = 0; p < passes; p++)
        {
            BlurLine(px, tmp, w, h, r, horizontal: true);
            BlurLine(tmp, px, w, h, r, horizontal: false);
        }
    }

    static void BlurLine(uint[] src, uint[] dst, int w, int h, int r, bool horizontal)
    {
        int lines = horizontal ? h : w, len = horizontal ? w : h;
        int step = horizontal ? 1 : w;
        int div = r * 2 + 1;
        for (int l = 0; l < lines; l++)
        {
            int start = horizontal ? l * w : l;
            int sr = 0, sg = 0, sb = 0;
            for (int k = -r; k <= r; k++)
            {
                uint c = src[start + Math.Clamp(k, 0, len - 1) * step];
                sr += (int)(c >> 16) & 255; sg += (int)(c >> 8) & 255; sb += (int)c & 255;
            }
            for (int i = 0; i < len; i++)
            {
                dst[start + i * step] = 0xFF000000u | (uint)(sr / div << 16) | (uint)(sg / div << 8) | (uint)(sb / div);
                uint outC = src[start + Math.Max(i - r, 0) * step];
                uint inC = src[start + Math.Min(i + r + 1, len - 1) * step];
                sr += ((int)(inC >> 16) & 255) - ((int)(outC >> 16) & 255);
                sg += ((int)(inC >> 8) & 255) - ((int)(outC >> 8) & 255);
                sb += ((int)inC & 255) - ((int)outC & 255);
            }
        }
    }

    public void Dispose()
    {
        if (!_shared)
        {
            DisposeCore();
            return;
        }
        lock (CacheSync)
        {
            if (--_references == 0)
            {
                if (_cachedInstance == this) { _cachedInstance = null; _cachedPath = null; }
                DisposeCore();
            }
        }
    }

    void DisposeCore()
    {
        lock (_sourceLock)
        {
            foreach (var b in _source) b.Dispose();
            _source.Clear();
        }
    }
}
