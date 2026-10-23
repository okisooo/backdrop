namespace Backdrop;

// Pixel rectangle, end-exclusive.
readonly record struct Rect(int X0, int Y0, int X1, int Y1)
{
    public static readonly Rect Empty = new(0, 0, 0, 0);
    public bool IsEmpty => X1 <= X0 || Y1 <= Y0;
    public Rect Union(Rect o) =>
        IsEmpty ? o : o.IsEmpty ? this : new(Math.Min(X0, o.X0), Math.Min(Y0, o.Y0), Math.Max(X1, o.X1), Math.Max(Y1, o.Y1));
}

// Corner radii of a rounded rectangle, in pixels.
readonly record struct Corners(int TopLeft, int TopRight, int BottomRight, int BottomLeft)
{
    public static readonly Corners None = default;
}

readonly struct BackgroundKeys
{
    public readonly int[] Keys;       // 0xRRGGBB; Keys[0] is the chat background
    public readonly Rect Keyed;       // only pixels inside it, and inside its rounded corners, are keyed
    public readonly Corners Radii;
    public readonly bool Panel;       // layout (Codex panel or Claude/T3 sidebar) was found
    public readonly Rect Pane;        // passed through unchanged; empty when there is none

    public BackgroundKeys(int[] keys, Rect keyed, Corners radii = default, bool panel = false, Rect pane = default)
    {
        Keys = keys; Keyed = keyed; Radii = radii; Panel = panel; Pane = pane;
    }

    public bool IsSet => Keys != null;

    public bool Contains(int x, int y)
    {
        var k = Keyed;
        if (x < k.X0 || y < k.Y0 || x >= k.X1 || y >= k.Y1) return false;
        if (x >= Pane.X0 && x < Pane.X1 && y >= Pane.Y0 && y < Pane.Y1) return false;
        return InCorner(x, y, k.X0, k.Y0, Radii.TopLeft, 1, 1) && InCorner(x, y, k.X1, k.Y0, Radii.TopRight, -1, 1) &&
               InCorner(x, y, k.X1, k.Y1, Radii.BottomRight, -1, -1) && InCorner(x, y, k.X0, k.Y1, Radii.BottomLeft, 1, -1);
    }

    static bool InCorner(int x, int y, int cx, int cy, int r, int dx, int dy)
    {
        if (r <= 0) return true;
        float px = x + 0.5f, py = y + 0.5f;
        float ox = cx + dx * r, oy = cy + dy * r;
        if ((px - ox) * dx >= 0 || (py - oy) * dy >= 0) return true;
        return (px - ox) * (px - ox) + (py - oy) * (py - oy) <= r * r;
    }

    public bool SameAs(BackgroundKeys other) =>
        Keyed == other.Keyed && Radii == other.Radii && Panel == other.Panel && Pane == other.Pane &&
        Keys != null && other.Keys != null && Keys.AsSpan().SequenceEqual(other.Keys);

    public override string ToString() =>
        $"background {string.Join(", ", Keys.Select(k => $"#{k:X6}"))}, wallpaper in {Keyed.X0},{Keyed.Y0} {Keyed.X1 - Keyed.X0}x{Keyed.Y1 - Keyed.Y0}" +
        (Radii == Corners.None ? "" : $" (corners {Radii.TopLeft}/{Radii.TopRight}/{Radii.BottomRight}/{Radii.BottomLeft})") +
        (Pane.IsEmpty ? "" : $", pane {Pane.X0},{Pane.Y0} {Pane.X1 - Pane.X0}x{Pane.Y1 - Pane.Y0}");
}

enum FrameKind { Page, Dialog, Media }

static unsafe class KeyDetector
{
    public static BackgroundKeys? Detect(byte* rows, int pitch, int w, int h, bool blendSidebar, int current = -1, TargetApp app = TargetApp.Claude)
    {
        int tol = app == TargetApp.T3Code ? 4 : 0;
        bool bucketed = app == TargetApp.T3Code;

        int top = current >= 0 && Share(rows, pitch, 0, w, h, 5, current, tol) >= 0.1
            ? current
            : Dominant(rows, pitch, 0, w, h, 5, 0.2, bucketed) ?? -1;
        if (top < 0) return null;

        if (app == TargetApp.Codex)
        {
            Rect panel = FindPanel(rows, pitch, w, h, top);
            bool found = !panel.IsEmpty;
            if (!blendSidebar && found)
                return new BackgroundKeys(new[] { top }, panel, FindCorners(rows, pitch, panel, top), true);

            int codexSidebar = Dominant(rows, pitch, 0, Math.Min(w / 4, 160), h, 3, 0.3, false) ?? top;
            bool isCodexSidebar = codexSidebar != top && Compositor.Dist((codexSidebar >> 16) & 255, (codexSidebar >> 8) & 255, codexSidebar & 255, top) <= 24;
            int[] codexKeys = isCodexSidebar ? new[] { top, codexSidebar } : new[] { top };
            Rect codexPane = found && w - panel.X1 >= 240 ? new Rect(panel.X1, 0, w, h) : Rect.Empty;
            return new BackgroundKeys(codexKeys, new Rect(0, 0, w, h), Corners.None, found, codexPane);
        }

        int sidebar = Dominant(rows, pitch, 0, Math.Min(w / 4, 160), h, 3, 0.3, bucketed) ?? top;
        int sideDist = Compositor.Dist((sidebar >> 16) & 255, (sidebar >> 8) & 255, sidebar & 255, top);
        int minSideDist = app == TargetApp.T3Code ? 6 : 1;
        int maxSideDist = app == TargetApp.T3Code ? 38 : 24;
        bool isSidebar = sideDist >= minSideDist && sideDist <= maxSideDist;
        int solidRight = isSidebar && !blendSidebar ? FindSidebar(rows, pitch, w, h, sidebar, top, tol) : 0;
        int[] keys = isSidebar && blendSidebar ? new[] { top, sidebar } : new[] { top };
        bool hasSidebar = solidRight > 0 || keys.Length > 1;
        Rect pane = FindPane(rows, pitch, w, h, top, solidRight, tol);
        return new BackgroundKeys(keys, new Rect(solidRight, 0, w, h), Corners.None, hasSidebar, pane);
    }

    public static double OverlayRatio(byte* rows, int pitch, int w, int h, int now, int before, int tol = 0)
    {
        double Ratio(int shift) => (((now >> shift) & 255) + 0.5) / (((before >> shift) & 255) + 0.5);
        double r = Ratio(16), g = Ratio(8), b = Ratio(0);
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        if (Math.Max((now >> 16) & 255, Math.Max((now >> 8) & 255, now & 255)) <= 2) return 0;
        if (max > 0.97 || max - min >= 0.03) return -1;
        if (min >= 0.25) return min;

        if (Share(rows, pitch, 0, w, h, 5, before, tol) > 0.001) return min;
        int limit = (int)(max * 255) + 40;
        for (int y = 0; y < h; y += 2)
        {
            byte* p = rows + (long)y * pitch;
            for (int x = 0; x < Math.Min(64, w); x++, p += 4)
                if (Math.Max(p[0], Math.Max(p[1], p[2])) > limit) return -1;
        }
        return min;
    }

    public static FrameKind Classify(byte* rows, int pitch, int w, int h, BackgroundKeys found, int current, TargetApp app = TargetApp.Claude)
    {
        int tol = app == TargetApp.T3Code ? 4 : 0;
        int now = found.Keys[0];
        if (current < 0) return IsThemeBackground(now, app) ? FrameKind.Page : FrameKind.Media;
        if (HasDimmedBackdrop(rows, pitch, w, h, current)) return FrameKind.Dialog;
        if (Compositor.Dist((now >> 16) & 255, (now >> 8) & 255, now & 255, current) <= tol) return FrameKind.Page;
        double overlay = OverlayRatio(rows, pitch, w, h, now, current, tol);
        if (overlay >= 0.25) return FrameKind.Dialog;
        if (overlay >= 0 || !IsThemeBackground(now, app) || !found.Panel) return FrameKind.Media;
        return FrameKind.Page;
    }

    static bool HasDimmedBackdrop(byte* rows, int pitch, int w, int h, int current)
    {
        int opaque = 0, dimmed = 0, edgeOpaque = 0, edgeDimmed = 0;
        for (int gy = 0; gy < 10; gy++)
        for (int gx = 0; gx < 14; gx++)
        {
            int x = Math.Min(w - 1, (2 * gx + 1) * w / 28);
            int y = Math.Min(h - 1, (2 * gy + 1) * h / 20);
            byte* p = rows + (long)y * pitch + x * 4;
            if (p[3] != 255) continue;
            opaque++;
            bool edge = gx == 0 || gx == 13 || gy == 0 || gy == 9;
            if (edge) edgeOpaque++;
            double r = (p[2] + 0.5) / (((current >> 16) & 255) + 0.5);
            double g = (p[1] + 0.5) / (((current >> 8) & 255) + 0.5);
            double b = (p[0] + 0.5) / ((current & 255) + 0.5);
            double min = Math.Min(r, Math.Min(g, b)), max = Math.Max(r, Math.Max(g, b));
            if (min >= 0.25 && max <= 0.85 && max - min < 0.03)
            {
                dimmed++;
                if (edge) edgeDimmed++;
            }
        }
        return opaque > 0 && edgeOpaque > 0 && dimmed * 5 >= opaque * 2 && edgeDimmed * 4 >= edgeOpaque;
    }

    public static bool IsThemeBackground(int c, TargetApp app = TargetApp.Claude)
    {
        int r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255;
        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        if (app == TargetApp.T3Code)
        {
            if (max - min > 48 || min >= 255) return false;
            return max >= 175 || (max >= 8 && max <= 85);
        }
        if (max - min > 10) return false;
        if (app == TargetApp.Claude && min >= 254) return false;
        return max >= 200 || (max >= 20 && max <= 72);
    }

    static Rect FindPanel(byte* rows, int pitch, int w, int h, int key)
    {
        const double Min = 0.25, RowMin = 0.1;
        bool IsKey(int x, int y)
        {
            byte* p = rows + (long)y * pitch + x * 4;
            return ((p[2] << 16) | (p[1] << 8) | p[0]) == key;
        }
        double ColumnShare(int x, int y0, int y1)
        {
            int hits = 0, n = 0;
            for (int y = y0; y < y1; y += 4, n++) if (IsKey(x, y)) hits++;
            return n == 0 ? 0 : (double)hits / n;
        }
        double RowShare(int y, int x0, int x1)
        {
            int hits = 0, n = 0;
            for (int x = x0; x < x1; x += 4, n++) if (IsKey(x, y)) hits++;
            return n == 0 ? 0 : (double)hits / n;
        }

        int band0 = h / 8, band1 = h - h / 8;
        int left = -1;
        for (int x = 0; x + 3 < w * 3 / 4; x++)
            if (ColumnShare(x, band0, band1) >= Min && ColumnShare(x + 3, band0, band1) >= Min) { left = x; break; }
        if (left < 0) return Rect.Empty;
        int right = w;
        while (right > left && ColumnShare(right - 1, band0, band1) < Min) right--;
        int top = 0, bottom = h;
        while (top < h / 2 && RowShare(top, left, right) < RowMin) top++;
        while (bottom > h / 2 && RowShare(bottom - 1, left, right) < RowMin) bottom--;
        if (left < 4 || top < 4 || right - left < w / 4 || bottom - top < h / 3) return Rect.Empty;
        return new Rect(left, top, right, bottom);
    }

    static Corners FindCorners(byte* rows, int pitch, Rect r, int key)
    {
        int Dist(int x, int y)
        {
            byte* p = rows + (long)y * pitch + x * 4;
            return Compositor.Dist(p[2], p[1], p[0], key);
        }
        int Radius(int x, int y, int dx, int dy)
        {
            int frame = Dist(x, y);
            if (frame == 0) return 0;
            int near = frame / 2;
            for (int d = 1; d < 16; d++)
            {
                int px = x + d * dx, py = y + d * dy;
                if (px < r.X0 || px >= r.X1 || py < r.Y0 || py >= r.Y1) break;
                if (Dist(px, py) <= near) return (int)Math.Round((d - 0.5) / (1 - 1 / Math.Sqrt(2)));
            }
            return 0;
        }
        return new Corners(Radius(r.X0, r.Y0, 1, 1), Radius(r.X1 - 1, r.Y0, -1, 1),
            Radius(r.X1 - 1, r.Y1 - 1, -1, -1), Radius(r.X0, r.Y1 - 1, 1, -1));
    }

    static Rect FindPane(byte* rows, int pitch, int w, int h, int key, int from, int tol)
    {
        const int MinWidth = 240, MinHeight = 200;
        bool IsKey(int x, int y)
        {
            byte* p = rows + (long)y * pitch + x * 4;
            return Compositor.Dist(p[2], p[1], p[0], key) <= tol;
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

    static int FindSidebar(byte* rows, int pitch, int w, int h, int sidebar, int background, int tol)
    {
        double ColumnShare(int x, int color)
        {
            int hits = 0, n = 0;
            for (int y = 0; y < h; y += 6, n++)
            {
                byte* p = rows + (long)y * pitch + x * 4;
                if (Compositor.Dist(p[2], p[1], p[0], color) <= tol) hits++;
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

    static int? Dominant(byte* rows, int pitch, int x0, int x1, int h, int step, double share, bool bucketed)
    {
        if (!bucketed)
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

        var buckets = new Dictionary<int, int>();
        var exact = new Dictionary<int, int>();
        int bTotal = 0;
        for (int y = 0; y < h; y += step)
        {
            byte* row = rows + (long)y * pitch;
            for (int x = x0; x < x1; x += step)
            {
                byte* p = row + x * 4;
                if (p[3] != 255) continue;
                int c = (p[2] << 16) | (p[1] << 8) | p[0];
                int b = ((p[2] >> 2) << 12) | ((p[1] >> 2) << 6) | (p[0] >> 2);
                buckets[b] = buckets.GetValueOrDefault(b) + 1;
                exact[c] = exact.GetValueOrDefault(c) + 1;
                bTotal++;
            }
        }
        if (bTotal == 0) return null;
        var bestBucket = buckets.MaxBy(kv => kv.Value);
        if (bestBucket.Value < bTotal * share) return null;
        int bestColor = 0, bestCount = -1;
        foreach (var kv in exact)
        {
            int r = (kv.Key >> 16) & 255, g = (kv.Key >> 8) & 255, bl = kv.Key & 255;
            int b = ((r >> 2) << 12) | ((g >> 2) << 6) | (bl >> 2);
            if (b == bestBucket.Key && kv.Value > bestCount) { bestColor = kv.Key; bestCount = kv.Value; }
        }
        return bestColor;
    }

    static double Share(byte* rows, int pitch, int x0, int x1, int h, int step, int color, int tol)
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
                if (Compositor.Dist(p[2], p[1], p[0], color) <= tol) hits++;
            }
        }
        return total == 0 ? 0 : (double)hits / total;
    }
}
sealed unsafe class Compositor
{
    int _w, _h;
    byte[] _k = Array.Empty<byte>(), _cov = Array.Empty<byte>();
    byte[] _lut = Array.Empty<byte>();   // [key][channel b,g,r][value] -> ink opacity 0..255
    int[] _keys = { 0xFFFFFF };
    BackgroundKeys _background;
    double _wash;

    // Wallpaper sampling (bilinear from the small prepared frame, washed toward the background).
    uint[] _raw = Array.Empty<uint>(), _small = Array.Empty<uint>();
    int _sw, _sh, _mapW = -1, _mapSw = -1;
    int[] _sx0 = Array.Empty<int>(), _sx1 = Array.Empty<int>(), _fx = Array.Empty<int>();
    uint[] _vrow = Array.Empty<uint>();

    public Compositor() => SetKeys(new BackgroundKeys(_keys, new Rect(0, 0, int.MaxValue, int.MaxValue)), 1);

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
        _background = background;
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
                else if (diff > 0) a = (diff * 255 + (255 - bc) / 2) / Math.Max(1, 255 - bc);
                else a = (-diff * 255 + bc / 2) / Math.Max(1, bc);
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
        int w = _w, keyCount = _keys.Length;
        var background = _background;
        fixed (byte* lut = _lut, k = _k, cov = _cov)
        {
            for (int y = changed.Y0; y < changed.Y1; y++)
            {
                int i = y * w + changed.X0;
                byte* p = cap + (long)i * 4;
                for (int x = changed.X0; x < changed.X1; x++, i++, p += 4)
                {
                    if (p[3] != 255 || !background.Contains(x, y)) { cov[i] = 255; k[i] = 0; continue; }
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
                        dst[i] = *(uint*)p; // Codex's own pixel (transparent outside the window shape)
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