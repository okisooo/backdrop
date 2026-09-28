namespace ClaudeBackdrop;

// Fallback renderer for systems where the GPU path cannot start: reads each captured
// frame back to the CPU, recomposites only the pixels that changed, and shows the result
// through UpdateLayeredWindow.
sealed unsafe class CpuRenderer : IRenderer
{
    readonly GpuDevice _gpu;
    readonly Compositor _comp = new();
    byte[] _frame = Array.Empty<byte>(), _previous = Array.Empty<byte>();
    int _w, _h;
    bool _analyzed, _haveWallpaper;
    Rect _dirty;

    // Layered-window surface.
    IntPtr _memDC, _dib, _oldBitmap, _bits;
    int _surfaceW, _surfaceH, _x = int.MinValue, _y;

    public string Name => "CPU";
    public OverlayWindow Window { get; }
    public int Width => _w;
    public int Height => _h;

    public CpuRenderer(GpuDevice gpu)
    {
        _gpu = gpu;
        Window = new OverlayWindow(composition: false);
        _memDC = Native.CreateCompatibleDC(IntPtr.Zero);
    }

    public void SetCapture(IntPtr texture, int w, int h)
    {
        int n = w * h * 4;
        (_previous, _frame) = (_frame, _previous);
        if (_frame.Length != n) _frame = new byte[n];
        _gpu.Read(texture, w, h, (rows, pitch, rw, rh) =>
        {
            fixed (byte* dst = _frame)
            {
                int row = rw * 4;
                for (int y = 0; y < rh; y++) Buffer.MemoryCopy(rows + (long)y * pitch, dst + (long)y * row, row, row);
            }
        });
        bool comparable = _analyzed && w == _w && h == _h && _previous.Length == n;
        _comp.EnsureSize(w, h);
        _w = w; _h = h;
        Rect changed = comparable ? Compositor.Diff(_frame, _previous, w, h) : _comp.Full;
        if (changed.IsEmpty) return;
        fixed (byte* cap = _frame) _dirty = _dirty.Union(_comp.Analyze(cap, changed));
        _analyzed = true;
    }

    public void ReadCapture(GpuDevice.RowsReader reader)
    {
        if (!_analyzed) return;
        fixed (byte* p = _frame) reader(p, _w * 4, _w, _h);
    }

    public void SetKeys(BackgroundKeys keys, int tolerance)
    {
        _comp.SetKeys(keys, tolerance);
        if (!_analyzed) return;
        fixed (byte* cap = _frame) _comp.Analyze(cap, _comp.Full);
        _dirty = _comp.Full;
    }

    // The CPU renderer keeps the plain model, without calming behind content.
    public void SetCalm(double strength) { }

    public void SetWash(double amount)
    {
        _comp.SetWash(amount);
        _dirty = _comp.Full;
    }

    public void SetWallpaper(uint[] pixels, int sw, int sh)
    {
        _comp.SetWallpaper(pixels, sw, sh);
        _haveWallpaper = true;
        _dirty = _comp.Full;
    }

    public void Render(float opacity, int x, int y)
    {
        if (!_analyzed || !_haveWallpaper) return;
        if (EnsureSurface(_w, _h)) _dirty = _comp.Full;
        if (!_dirty.IsEmpty)
        {
            fixed (byte* cap = _frame) _comp.Compose(cap, (uint*)_bits, _dirty);
            _dirty = Rect.Empty;
        }
        var pos = new Native.POINT { X = x, Y = y };
        var size = new Native.SIZE { cx = _surfaceW, cy = _surfaceH };
        var src = new Native.POINT();
        var blend = new Native.BLENDFUNCTION { SourceConstantAlpha = (byte)Math.Clamp(opacity * 255f + 0.5f, 0, 255), AlphaFormat = 1 };
        IntPtr screen = Native.GetDC(IntPtr.Zero);
        try { Native.UpdateLayeredWindow(Window.Handle, screen, ref pos, ref size, _memDC, ref src, 0, ref blend, 2); }
        finally { Native.ReleaseDC(IntPtr.Zero, screen); }
        _x = x; _y = y;
    }

    public void Move(int x, int y)
    {
        if (_x == int.MinValue || (x == _x && y == _y)) return;
        Native.SetWindowPos(Window.Handle, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        _x = x; _y = y;
    }

    // Returns true when a new (blank) surface was created.
    bool EnsureSurface(int w, int h)
    {
        if (w == _surfaceW && h == _surfaceH && _dib != IntPtr.Zero) return false;
        ReleaseSurface();
        var bmi = new Native.BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        _dib = Native.CreateDIBSection(_memDC, ref bmi, 0, out _bits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero) throw new InvalidOperationException("CreateDIBSection failed");
        _oldBitmap = Native.SelectObject(_memDC, _dib);
        _surfaceW = w; _surfaceH = h;
        return true;
    }

    void ReleaseSurface()
    {
        if (_dib == IntPtr.Zero) return;
        Native.SelectObject(_memDC, _oldBitmap);
        Native.DeleteObject(_dib);
        _dib = _bits = IntPtr.Zero;
        _surfaceW = _surfaceH = 0;
    }

    public void Dispose()
    {
        ReleaseSurface();
        if (_memDC != IntPtr.Zero) { Native.DeleteDC(_memDC); _memDC = IntPtr.Zero; }
        Window.Dispose();
    }
}
