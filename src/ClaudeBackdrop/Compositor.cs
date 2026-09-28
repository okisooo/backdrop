namespace ClaudeBackdrop;

// Pixel rectangle, end-exclusive.
readonly record struct Rect(int X0, int Y0, int X1, int Y1)
{
    public static readonly Rect Empty = new(0, 0, 0, 0);
    public bool IsEmpty => X1 <= X0 || Y1 <= Y0;
    public Rect Union(Rect o) =>
        IsEmpty ? o : o.IsEmpty ? this : new(Math.Min(X0, o.X0), Math.Min(Y0, o.Y0), Math.Max(X1, o.X1), Math.Max(Y1, o.Y1));
}

// Claude's background colors in the current frame, plus the areas passed through unchanged:
// the solid sidebar strip and a docked pane of foreign content (an embedded web page).
readonly struct BackgroundKeys
{
    public readonly int[] Keys;       // 0xRRGGBB; Keys[0] is the chat background
    public readonly int SolidRight;   // columns [0, SolidRight) are passed through unchanged
    public readonly Rect Pane;        // passed through unchanged; empty when there is none

    public BackgroundKeys(int[] keys, int solidRight, Rect pane = default) { Keys = keys; SolidRight = solidRight; Pane = pane; }

    public bool IsSet => Keys != null;
    public bool HasSidebar => SolidRight > 0 || Keys.Length > 1;

    public bool SameAs(BackgroundKeys other) =>
        SolidRight == other.SolidRight && Pane == other.Pane &&
        Keys != null && other.Keys != null && Keys.AsSpan().SequenceEqual(other.Keys);

    public override string ToString() =>
        $"background {string.Join(", ", Keys.Select(k => $"#{k:X6}"))}, sidebar {SolidRight}px" +
        (Pane.IsEmpty ? "" : $", pane {Pane.X0},{Pane.Y0} {Pane.X1 - Pane.X0}x{Pane.Y1 - Pane.Y0}");
}

enum FrameKind { Page, Dialog, Media }

static unsafe class KeyDetector
{
    // Claude's background is the dominant flat color. The sidebar is a large strip of a
    // nearby shade along the left edge: keyed as well with blendSidebar, otherwise kept solid.
    // `current` is the background in use: it stays while it still covers a good part of the
    // window, so a large surface of a nearby shade (the new-chat composer) cannot take over.
    public static BackgroundKeys? Detect(byte* rows, int pitch, int w, int h, bool blendSidebar, int current = -1)
    {
        int top = current >= 0 && Share(rows, pitch, 0, w, h, 5, current) >= 0.1
            ? current
            : Dominant(rows, pitch, 0, w, h, 5, 0.2) ?? -1;
        if (top < 0) return null;

        // The sidebar color is read from the left strip only, so large surfaces elsewhere
        // (the new-chat composer, a code block) can never be mistaken for it.
        int sidebar = Dominant(rows, pitch, 0, Math.Min(w / 4, 160), h, 3, 0.3) ?? top;
        bool isSidebar = sidebar != top && Compositor.Dist((sidebar >> 16) & 255, (sidebar >> 8) & 255, sidebar & 255, top) <= 24;
        int solidRight = isSidebar && !blendSidebar ? FindSidebar(rows, pitch, w, h, sidebar, top) : 0;
        int[] keys = isSidebar && blendSidebar ? new[] { top, sidebar } : new[] { top };
        return new BackgroundKeys(keys, solidRight, FindPane(rows, pitch, w, h, top, solidRight));
    }

    // Whether the frame's background `now` is Claude's background `before` under an even dark
    // overlay (a dialog backdrop, an image or video viewer) rather than a new background.
    // Returns how much of the background shows through (0..1), or -1 when it is no overlay.
    public static double OverlayRatio(byte* rows, int pitch, int w, int h, int now, int before)
    {
        double Ratio(int shift) => (((now >> shift) & 255) + 0.5) / (((before >> shift) & 255) + 0.5);
        double r = Ratio(16), g = Ratio(8), b = Ratio(0);
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        // Pure black (a video viewer) is the deepest overlay, whatever the ratio says.
        if (Math.Max((now >> 16) & 255, Math.Max((now >> 8) & 255, now & 255)) <= 2) return 0;
        if (max > 0.97 || max - min >= 0.03) return -1;
        if (min >= 0.25) return min;

        // A deep overlay can match a dark theme's background. A theme switch replaces the old
        // background everywhere, so any of it still showing (caption buttons, a bar the overlay
        // leaves uncovered) means an overlay.
        if (Share(rows, pitch, 0, w, h, 5, before) > 0.001) return min;
        // Under an overlay nothing outside its own content is brighter than dimmed white; a dark
        // theme draws light icons and labels along the left edge (sidebar, title bar), where
        // overlay content never reaches.
        int limit = (int)(max * 255) + 40;
        for (int y = 0; y < h; y += 2)
        {
            byte* p = rows + (long)y * pitch;
            for (int x = 0; x < Math.Min(64, w); x++, p += 4)
                if (Math.Max(p[0], Math.Max(p[1], p[2])) > limit) return -1;
        }
        return min;
    }

    // What fills the window, given the frame's detection and the background in use (`current`,
    // -1 before there is one). A dialog backdrop darkens Claude's page evenly; the wallpaper
    // dims with it. Anything else that is not Claude's page (a video, a viewer's black
    // backdrop, a document or page of its own color) is media, shown untouched. A new
    // background counts as Claude's page (a theme switch) only when it is a theme gray and
    // Claude's layout shows.
    public static FrameKind Classify(byte* rows, int pitch, int w, int h, BackgroundKeys found, int current)
    {
        int now = found.Keys[0];
        if (current < 0) return IsThemeBackground(now) ? FrameKind.Page : FrameKind.Media;
        if (now == current) return FrameKind.Page;
        double overlay = OverlayRatio(rows, pitch, w, h, now, current);
        if (overlay >= 0.25) return FrameKind.Dialog;
        if (overlay >= 0 || !IsThemeBackground(now) || !found.HasSidebar) return FrameKind.Media;
        return FrameKind.Page;
    }

    // Claude's page backgrounds are near-neutral grays: an off-white in the light theme, a dark
    // gray in the dark theme. Pure black or white, and colored video frames, are not.
    public static bool IsThemeBackground(int c)
    {
        int r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255;
        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        if (max - min > 10 || min >= 254) return false;
        return max >= 200 || (max >= 20 && max <= 72);
    }

    // A pane docked at the right edge that shows none of Claude's background: an embedded web
    // page (the browser, a preview) or another surface drawn on its own. Its wallpaper-free
    // content must stay as drawn, not be read as ink over Claude's background. The rectangle
    // is traced to the content's own edges (Claude's bars above and below it show the
    // background), so passing it through unchanged never cuts through a flat area.
    static Rect FindPane(byte* rows, int pitch, int w, int h, int key, int from)
    {
        const int MinWidth = 240, MinHeight = 200;
        bool IsKey(int x, int y)
        {
            byte* p = rows + (long)y * pitch + x * 4;
            return ((p[2] << 16) | (p[1] << 8) | p[0]) == key;
        }
        bool ColumnHasKey(int x, int y0, int y1)
        {
            int hits = 0, n = 0;
            for (int y = y0; y < y1; y += 4, n++) if (IsKey(x, y)) hits++;
            return hits > n / 50;
        }
        bool RowHasKey(int y, int x0, int x1)
        {
            int hits = 0, n = 0;
            for (int x = x0; x < x1; x += 4, n++) if (IsKey(x, y)) hits++;
            return hits > n / 50;
        }
        int LeftEdge(int y0, int y1)
        {
            int left = w;
            while (left > from && !ColumnHasKey(left - 1, y0, y1)) left--;
            return left;
        }

        int left = LeftEdge(h / 4, h);
        if (w - left < MinWidth) return Rect.Empty;
        int top = 0, bottom = h;
        for (int y = 0; y < h / 2; y++) if (RowHasKey(y, left, w)) top = y + 1;
        for (int y = h - 1; y >= h / 2; y--) if (RowHasKey(y, left, w)) bottom = y;
        if (bottom - top < MinHeight) return Rect.Empty;
        left = LeftEdge(top, bottom);
        return w - left < MinWidth ? Rect.Empty : new Rect(left, top, w, bottom);
    }

    // Most common opaque color in columns [x0, x1), when it covers at least `share` of them.
    static int? Dominant(byte* rows, int pitch, int x0, int x1, int h, int step, double share)
    {
        var hist = new Dictionary<int, int>();
        int total = 0;
        for (int y = 0; y < h; y += step)
        {
            byte* row = rows + (long)y * pitch;
            for (int x = x0; x < x1; x += step)
            {
                byte* p = row + x * 4;
                if (p[3] != 255) continue;
                int c = (p[2] << 16) | (p[1] << 8) | p[0];
                hist[c] = hist.GetValueOrDefault(c) + 1;
                total++;
            }
        }
        if (total == 0) return null;
        var best = hist.MaxBy(kv => kv.Value);
        return best.Value >= total * share ? best.Key : null;
    }

    // Fraction of opaque samples in columns [x0, x1) that are exactly `color`.
    static double Share(byte* rows, int pitch, int x0, int x1, int h, int step, int color)
    {
        int hits = 0, total = 0;
        for (int y = 0; y < h; y += step)
        {
            byte* row = rows + (long)y * pitch;
            for (int x = x0; x < x1; x += step)
            {
                byte* p = row + x * 4;
                if (p[3] != 255) continue;
                total++;
                if (((p[2] << 16) | (p[1] << 8) | p[0]) == color) hits++;
            }
        }
        return total == 0 ? 0 : (double)hits / total;
    }

    // Right edge of a left-anchored strip whose columns are mostly the sidebar color,
    // including the divider line that separates it from the chat. 0 when there is none.
    static int FindSidebar(byte* rows, int pitch, int w, int h, int sidebar, int background)
    {
        double ColumnShare(int x, int color)
        {
            int hits = 0, n = 0;
            for (int y = 0; y < h; y += 6, n++)
            {
                byte* p = rows + (long)y * pitch + x * 4;
                if (((p[2] << 16) | (p[1] << 8) | p[0]) == color) hits++;
            }
            return n == 0 ? 0 : (double)hits / n;
        }

        int last = -1;
        for (int x = 0; x < w / 2; x++)
        {
            if (ColumnShare(x, sidebar) >= 0.3) last = x;
            else if (last >= 0 ? x - last > 3 : x > 24) break;
        }
        if (last < 120) return 0;
        int right = last + 1;
        while (right < w && right <= last + 3 && ColumnShare(right, background) < 0.3 && ColumnShare(right, sidebar) < 0.3) right++;
        return right;
    }
}

// CPU compositing for the fallback renderer: the GPU renderer's base model, without panel
// detection.
//
// Every Claude pixel P is read as ink F at opacity a drawn over Claude's flat background
// B (P = a*F + (1-a)*B), using the smallest a that can produce P ("color to alpha").
// Text stays nearly opaque, anti-aliased edges keep their partial coverage, and soft
// effects such as shadows and fade gradients become faint tints instead of solid bands.
// White surfaces stay fully solid because B is almost white. The output replaces B with
// the wallpaper W: a*F + (1-a)*W = P + (1-a)*(W-B).
sealed unsafe class Compositor
{
    int _w, _h;
    byte[] _k = Array.Empty<byte>(), _cov = Array.Empty<byte>();
    byte[] _lut = Array.Empty<byte>();   // [key][channel b,g,r][value] -> ink opacity 0..255
    int[] _keys = { 0xFCFCFB };
    int _solidRight;
    Rect _pane;
    double _wash;

    // Wallpaper sampling (bilinear from the small prepared frame, washed toward the background).
    uint[] _raw = Array.Empty<uint>(), _small = Array.Empty<uint>();
    int _sw, _sh, _mapW = -1, _mapSw = -1;
    int[] _sx0 = Array.Empty<int>(), _sx1 = Array.Empty<int>(), _fx = Array.Empty<int>();
    uint[] _vrow = Array.Empty<uint>();

    public Compositor() => SetKeys(new BackgroundKeys(_keys, 0), 1);

    public int Width => _w;
    public int Height => _h;
    public Rect Full => new(0, 0, _w, _h);

    public void EnsureSize(int w, int h)
    {
        if (w == _w && h == _h) return;
        _w = w; _h = h;
        _k = new byte[w * h];
        _cov = new byte[w * h];
        _mapW = -1;
    }

    public void SetKeys(BackgroundKeys background, int tolerance)
    {
        int[] keys = background.Keys;
        bool rewash = keys[0] != _keys[0];
        _keys = keys;
        _solidRight = background.SolidRight;
        _pane = background.Pane;
        if (rewash) Wash();
        var lut = new byte[keys.Length * 3 * 256];
        for (int k = 0; k < keys.Length; k++)
        for (int c = 0; c < 3; c++)
        {
            int bc = (keys[k] >> (c * 8)) & 255; // c: 0 = blue, 1 = green, 2 = red
            for (int v = 0; v < 256; v++)
            {
                int diff = v - bc, a;
                if (Math.Abs(diff) <= tolerance) a = 0;
                else if (diff > 0) a = (diff * 255 + (255 - bc) / 2) / (255 - bc);
                else a = (-diff * 255 + bc / 2) / bc;
                lut[(k * 3 + c) * 256 + v] = (byte)Math.Min(255, a);
            }
        }
        _lut = lut;
    }

    public void SetWallpaper(uint[] small, int sw, int sh)
    {
        _raw = small; _sw = sw; _sh = sh;
        if (_vrow.Length < sw) _vrow = new uint[sw];
        Wash();
    }

    public void SetWash(double amount)
    {
        _wash = amount;
        Wash();
    }

    void Wash()
    {
        if (_small.Length != _raw.Length) _small = new uint[_raw.Length];
        Wallpaper.Wash(_raw, _small, (uint)_keys[0], _wash);
    }

    // Re-analyzes the captured pixels inside `changed`. Each output pixel depends only on
    // the same input pixel, so exactly `changed` needs recomposing.
    public Rect Analyze(byte* cap, Rect changed)
    {
        int w = _w, keyCount = _keys.Length, solid = _solidRight;
        Rect pane = _pane;
        fixed (byte* lut = _lut, k = _k, cov = _cov)
        {
            for (int y = changed.Y0; y < changed.Y1; y++)
            {
                int i = y * w + changed.X0;
                byte* p = cap + (long)i * 4;
                for (int x = changed.X0; x < changed.X1; x++, i++, p += 4)
                {
                    bool inPane = x >= pane.X0 && x < pane.X1 && y >= pane.Y0 && y < pane.Y1;
                    if (x < solid || inPane || p[3] != 255) { cov[i] = 255; k[i] = 0; continue; }
                    int best = 256, bestKey = 0;
                    for (int key = 0; key < keyCount; key++)
                    {
                        byte* t = lut + key * 768;
                        int a = Math.Max(t[p[0]], Math.Max(t[256 + p[1]], t[512 + p[2]]));
                        if (a < best) { best = a; bestKey = key; }
                    }
                    cov[i] = (byte)best;
                    k[i] = (byte)bestKey;
                }
            }
        }
        return changed;
    }

    // Writes premultiplied BGRA output pixels for `area`.
    public void Compose(byte* cap, uint* dst, Rect area)
    {
        if (area.IsEmpty || _small.Length == 0) return;
        int w = _w, h = _h;
        int[] keys = _keys;
        EnsureWallpaperMap();

        fixed (byte* cov = _cov, k = _k)
        fixed (int* sx0 = _sx0, sx1 = _sx1, fx = _fx)
        fixed (uint* small = _small, vrow = _vrow)
        {
            for (int y = area.Y0; y < area.Y1; y++)
            {
                // Wallpaper row: blend the two nearest source rows once per output row.
                float sy = (y + 0.5f) * _sh / h - 0.5f;
                int sr0 = (int)MathF.Floor(sy);
                int fy = (int)((sy - sr0) * 256);
                if (sr0 < 0) { sr0 = 0; fy = 0; }
                if (sr0 >= _sh - 1) { sr0 = _sh - 1; fy = 0; }
                int sr1 = Math.Min(sr0 + 1, _sh - 1);
                uint* rowA = small + sr0 * _sw, rowB = small + sr1 * _sw;
                for (int j = sx0[area.X0], jEnd = sx1[area.X1 - 1]; j <= jEnd; j++)
                    vrow[j] = Lerp(rowA[j], rowB[j], fy);

                int i = y * w + area.X0;
                byte* p = cap + (long)i * 4;
                for (int x = area.X0; x < area.X1; x++, i++, p += 4)
                {
                    int c = cov[i];
                    if (c == 255)
                    {
                        dst[i] = *(uint*)p; // Claude's own pixel (transparent outside the window shape)
                        continue;
                    }
                    uint wp = Lerp(vrow[sx0[x]], vrow[sx1[x]], fx[x]);
                    if (c == 0)
                    {
                        dst[i] = wp | 0xFF000000u;
                        continue;
                    }
                    int ia = 255 - c;
                    int key = keys[k[i]];
                    int rr = Math.Clamp(p[2] + (((int)((wp >> 16) & 255) - ((key >> 16) & 255)) * ia + 127) / 255, 0, 255);
                    int gg = Math.Clamp(p[1] + (((int)((wp >> 8) & 255) - ((key >> 8) & 255)) * ia + 127) / 255, 0, 255);
                    int bb = Math.Clamp(p[0] + (((int)(wp & 255) - (key & 255)) * ia + 127) / 255, 0, 255);
                    dst[i] = 0xFF000000u | ((uint)rr << 16) | ((uint)gg << 8) | (uint)bb;
                }
            }
        }
    }

    void EnsureWallpaperMap()
    {
        if (_mapW == _w && _mapSw == _sw && _sx0.Length == _w) return;
        _mapW = _w; _mapSw = _sw;
        _sx0 = new int[_w]; _sx1 = new int[_w]; _fx = new int[_w];
        for (int x = 0; x < _w; x++)
        {
            float sx = (x + 0.5f) * _sw / _w - 0.5f;
            int s0 = (int)MathF.Floor(sx);
            int f = (int)((sx - s0) * 256);
            if (s0 < 0) { s0 = 0; f = 0; }
            if (s0 >= _sw - 1) { s0 = _sw - 1; f = 0; }
            _sx0[x] = s0; _sx1[x] = Math.Min(s0 + 1, _sw - 1); _fx[x] = f;
        }
    }

    static uint Lerp(uint a, uint b, int f)
    {
        uint g = (uint)f, ig = 256u - g;
        uint rb = (((a & 0x00FF00FFu) * ig + (b & 0x00FF00FFu) * g) >> 8) & 0x00FF00FFu;
        uint ag = (((a >> 8) & 0x00FF00FFu) * ig + ((b >> 8) & 0x00FF00FFu) * g) & 0xFF00FF00u;
        return rb | ag;
    }

    public static int Dist(int r, int g, int b, int key)
    {
        int dr = Math.Abs(r - ((key >> 16) & 255));
        int dg = Math.Abs(g - ((key >> 8) & 255));
        int db = Math.Abs(b - (key & 255));
        return Math.Max(dr, Math.Max(dg, db));
    }

    // Bounding box of pixels that differ between two BGRA frames of the same size.
    public static Rect Diff(byte[] a, byte[] b, int w, int h)
    {
        int stride = w * 4;
        int y0 = -1, y1 = -1, x0 = w, x1 = 0;
        for (int y = 0; y < h; y++)
        {
            var ra = new ReadOnlySpan<byte>(a, y * stride, stride);
            var rb = new ReadOnlySpan<byte>(b, y * stride, stride);
            int first = ra.CommonPrefixLength(rb);
            if (first == stride) continue;
            if (y0 < 0) y0 = y;
            y1 = y;
            x0 = Math.Min(x0, first / 4);
            int end = stride;
            while (end > first)
            {
                int chunk = Math.Min(64, end - first);
                if (!ra.Slice(end - chunk, chunk).SequenceEqual(rb.Slice(end - chunk, chunk)))
                {
                    int q = end - 1;
                    while (ra[q] == rb[q]) q--;
                    end = q;
                    break;
                }
                end -= chunk;
            }
            x1 = Math.Max(x1, end / 4 + 1);
        }
        return y0 < 0 ? Rect.Empty : new Rect(x0, y0, Math.Max(x1, x0 + 1), y1 + 1);
    }
}
