using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClaudeBackdrop;

// The engine. One thread owns everything that touches the Claude window: the overlay
// window, the window-event hooks, the capture session and the renderer, so no window
// call ever crosses threads. The tray UI only passes settings in and reads Status.
sealed unsafe class Backdrop : IDisposable
{
    const int PollMs = 500;
    const int FadeInMs = 160;
    const int FreshFrameWaitMs = 150;
    const int SystemAnimationMs = 260;   // restore / desktop-switch animation to wait out

    readonly Thread _thread;
    readonly AutoResetEvent _wake = new(false);
    volatile bool _stop;
    volatile Settings _pendingSettings;

    public string Status { get; private set; } = "Starting";

    public Backdrop(Settings settings)
    {
        _pendingSettings = settings.Clone();
        _thread = new Thread(Run) { IsBackground = true, Name = "ClaudeBackdrop engine" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void UpdateSettings(Settings s)
    {
        _pendingSettings = s.Clone();
        _wake.Set();
    }

    // ---- Engine thread state ----------------------------------------------------------

    readonly Stopwatch _clock = Stopwatch.StartNew();
    Settings _settings;
    Native.WinEventProc _hookProc;
    IntPtr _foregroundHook;
    readonly List<IntPtr> _hooks = new();

    GpuDevice _gpu;
    WindowCapture _capture;
    IRenderer _renderer;
    IntPtr _claude;
    int _gpuFailures;
    long _retryAt, _nextPoll;
    bool _resumed;

    // Visibility: the overlay shows only when Claude is visible and a fresh frame is ready,
    // then fades in, so it never appears over a restore animation or with outdated content.
    bool _wantVisible, _visible, _inMoveSize, _covered;
    int _claudeW, _claudeH;
    long _showRequestedAt = -1, _showNotBefore;
    bool _freshSinceRequest, _returningFromHidden;
    long _fadeStart = -1, _fadeOutStart = -1;
    float _opacity, _fadeFrom;

    // Media (a video, an image viewer) fills the window: the copy fades out and Claude shows
    // untouched until its page is back.
    bool _media, _pendingMedia;

    bool _haveFrame, _keysDirty = true, _redraw;
    long _lastKeyScan;
    BackgroundKeys _keys, _pendingKeys;

    Wallpaper _wall;
    string _wallPath;
    PreparedWallpaper _prepared, _shownPrepared;
    Task<PreparedWallpaper> _prepTask;
    int _frameIndex, _shownFrame = -1;
    long _nextFrameAt;

    long _statSince, _statRenders, _statFrames, _statDelay, _statDelayMax, _statWork;
    long _frameArrived;

    void Run()
    {
        _hookProc = OnWinEvent;
        _foregroundHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            _hookProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        var handles = new[] { _wake.SafeWaitHandle.DangerousGetHandle() };
        while (!_stop)
        {
            Native.MsgWaitForMultipleObjectsEx(1, handles, (uint)NextTimeout(), Native.QS_ALLINPUT, Native.MWMO_INPUTAVAILABLE);
            PumpMessages();
            if (_stop) break;
            try { Step(); }
            catch (Exception ex) { Fail(ex); }
        }
        Detach();
        if (_foregroundHook != IntPtr.Zero) Native.UnhookWinEvent(_foregroundHook);
        try { _prepTask?.Wait(2000); } catch { }
        _wall?.Dispose();
        _gpu?.Dispose();
    }

    void PumpMessages()
    {
        while (Native.PeekMessage(out var msg, IntPtr.Zero, 0, 0, Native.PM_REMOVE))
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }
    }

    int NextTimeout()
    {
        long now = _clock.ElapsedMilliseconds;
        long t = _nextPoll - now;
        if (_fadeStart >= 0 || _fadeOutStart >= 0 || _showRequestedAt >= 0) t = Math.Min(t, 8);
        if (Animating) t = Math.Min(t, _nextFrameAt - now);
        if (_pendingKeys.IsSet || _pendingMedia) t = Math.Min(t, _lastKeyScan + 260 - now);
        return (int)Math.Clamp(t, 0, PollMs);
    }

    bool Animating => _visible && !_covered && _prepared != null && _prepared.Frames.Length > 1 && _settings is { Animate: true };

    void Step()
    {
        long now = _clock.ElapsedMilliseconds;
        var pending = _pendingSettings;
        if (pending != null && !ReferenceEquals(pending, _settings)) ApplySettings(pending);
        if (_resumed)
        {
            // After sleep the capture session and the device may be stale; rebuild both.
            _resumed = false;
            Log("resumed from sleep; reattaching");
            Detach();
            DropDevice();
            _retryAt = now + 1500;
        }
        if (now >= _nextPoll)
        {
            _nextPoll = now + PollMs;
            Poll(now);
        }
        if (_renderer == null) return;

        long started = Stopwatch.GetTimestamp();
        bool changed = TakeFrame();
        if (_capture?.Closed == true) { Detach(); return; }
        if (!_haveFrame) return;

        // A pending change is re-checked even if Claude stops drawing, so it still settles.
        if (_keysDirty || ((changed || _pendingKeys.IsSet || _pendingMedia) && now - _lastKeyScan > 250))
        {
            _lastKeyScan = now;
            _keysDirty = false;
            changed |= DetectKeys();
        }
        if (!_keys.IsSet) return;
        changed |= UpdateWallpaper(now);
        changed |= UpdateVisibility(now);
        changed |= _redraw;
        _redraw = false;

        if (changed && _prepared != null && (_visible || _showRequestedAt >= 0) &&
            TryGetOrigin(_claude, _renderer.Width, _renderer.Height, out int x, out int y))
        {
            _renderer.Render(_visible ? _opacity : 0, x, y);
            long done = Stopwatch.GetTimestamp();
            _statRenders++;
            _statWork += done - started;
            if (_frameArrived != 0)
            {
                // Time from a captured frame reaching this process to its composited copy
                // being handed to the compositor.
                long delay = done - _frameArrived;
                _statFrames++;
                _statDelay += delay;
                _statDelayMax = Math.Max(_statDelayMax, delay);
                _frameArrived = 0;
            }
        }
        LogStats(now);
    }

    void ApplySettings(Settings s)
    {
        bool first = _settings == null;
        _settings = s;
        if (_wallPath != s.BackgroundPath)
        {
            var old = _wall;
            if (old != null)
            {
                // A running preparation may still read the old frames.
                if (_prepTask != null) _prepTask.ContinueWith(_ => old.Dispose());
                else old.Dispose();
            }
            _wall = null;
            _wallPath = s.BackgroundPath;
            _frameIndex = 0;
            if (!string.IsNullOrEmpty(_wallPath) && File.Exists(_wallPath))
            {
                try { _wall = Wallpaper.Load(_wallPath); }
                catch (Exception ex) { Log("wallpaper load failed: " + ex.Message); }
            }
        }
        _keysDirty = true;
        _redraw = true;
        if (_renderer != null)
        {
            _renderer.SetCalm(s.Calm);
            _renderer.SetWash(s.Wash);
            if (_keys.IsSet) _renderer.SetKeys(_keys, s.KeyTolerance);
        }
        if (!first && _renderer != null && s.Renderer != (_renderer is CpuRenderer ? "cpu" : "auto"))
        {
            Detach(); // switch renderer on the next poll
            _gpuFailures = 0;
        }
        _nextPoll = 0;
    }

    // ---- Attach / detach -----------------------------------------------------------------

    void Poll(long now)
    {
        var s = _settings;
        if (!s.Enabled)
        {
            if (_claude != IntPtr.Zero) Detach();
            Status = "Paused";
            return;
        }
        if (_claude != IntPtr.Zero && !Native.IsWindow(_claude)) Detach();
        if (_claude == IntPtr.Zero)
        {
            if (now < _retryAt) return;
            IntPtr h = ClaudeLocator.FindMainWindow();
            if (h == IntPtr.Zero) { Status = "Waiting for Claude"; return; }
            Attach(h);
            if (_claude == IntPtr.Zero) return;
        }
        RefreshVisibility();
        if (_wall == null)
            Status = string.IsNullOrEmpty(s.BackgroundPath) ? "Choose a background" : "Background could not be loaded";
    }

    void Attach(IntPtr h)
    {
        try
        {
            _gpu ??= new GpuDevice();
            _renderer = CreateRenderer();
            _claude = h;
            _renderer.Window.SetOwner(h);
            _renderer.SetCalm(_settings.Calm);
            _renderer.SetWash(_settings.Wash);
            _renderer.Window.Resumed += OnResumed;
            _capture = new WindowCapture(h, _gpu.WinRTDevice, () => _wake.Set());
            Native.GetWindowThreadProcessId(h, out uint pid);
            foreach (var (min, max) in new[]
            {
                (Native.EVENT_OBJECT_DESTROY, Native.EVENT_OBJECT_LOCATIONCHANGE),
                (Native.EVENT_OBJECT_CLOAKED, Native.EVENT_OBJECT_UNCLOAKED),
                (Native.EVENT_SYSTEM_MOVESIZESTART, Native.EVENT_SYSTEM_MOVESIZEEND),
                (Native.EVENT_SYSTEM_MINIMIZESTART, Native.EVENT_SYSTEM_MINIMIZEEND),
            })
                _hooks.Add(Native.SetWinEventHook(min, max, IntPtr.Zero, _hookProc, pid, 0, Native.WINEVENT_OUTOFCONTEXT));
            _haveFrame = false;
            _keysDirty = true;
            _keys = default;
            _shownFrame = -1;
            _shownPrepared = null;
            _visible = false;
            _inMoveSize = false;
            _claudeW = _claudeH = 0;
            OnClaudeMoved();
            Status = $"Attached ({_renderer.Name})";
            Log($"attached to Claude window 0x{h:X} with the {_renderer.Name} renderer");
        }
        catch (Exception ex)
        {
            Log("attach failed: " + ex);
            Detach();
            DropDevice();
            Status = "Capture failed; retrying";
            _retryAt = _clock.ElapsedMilliseconds + 3000;
        }
    }

    IRenderer CreateRenderer()
    {
        if (_settings.Renderer != "cpu" && _gpuFailures < 3)
        {
            try { return new GpuRenderer(_gpu); }
            catch (Exception ex)
            {
                _gpuFailures++;
                Log("GPU renderer unavailable, using the CPU renderer: " + ex.Message);
            }
        }
        return new CpuRenderer(_gpu);
    }

    void Detach()
    {
        foreach (var hook in _hooks) if (hook != IntPtr.Zero) Native.UnhookWinEvent(hook);
        _hooks.Clear();
        _capture?.Dispose();
        _capture = null;
        if (_renderer != null)
        {
            _renderer.Window.Resumed -= OnResumed;
            _renderer.Dispose();
            _renderer = null;
        }
        _claude = IntPtr.Zero;
        _visible = false;
        _haveFrame = false;
        _showRequestedAt = -1;
        _fadeStart = -1;
    }

    void DropDevice()
    {
        _gpu?.Dispose();
        _gpu = null;
    }

    // Device lost (driver update, sleep), capture failure or a renderer bug: start over
    // with a fresh device. Repeated GPU failures fall back to the CPU renderer.
    void Fail(Exception ex)
    {
        Log("engine failure: " + ex);
        if (_renderer is GpuRenderer) _gpuFailures++;
        Detach();
        DropDevice();
        Status = "Recovering";
        _retryAt = _clock.ElapsedMilliseconds + 1000;
    }

    // Raised inside the overlay's window procedure, so the rebuild happens in Step.
    void OnResumed() => _resumed = true;

    // ---- Window events -------------------------------------------------------------------

    void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (evt == Native.EVENT_SYSTEM_FOREGROUND)
            {
                // Any activation can uncover or cover Claude; re-check right away so the
                // wallpaper resumes the moment the user tabs back in.
                if (_claude != IntPtr.Zero) RefreshVisibility();
                return;
            }
            if (hwnd != _claude || idObject != 0 || idChild != 0) return;
            switch (evt)
            {
                case Native.EVENT_OBJECT_DESTROY:
                    Detach();
                    break;
                case Native.EVENT_OBJECT_LOCATIONCHANGE:
                    OnClaudeMoved();
                    break;
                case Native.EVENT_SYSTEM_MOVESIZESTART:
                    // Show plain Claude while the window is dragged or resized; the copy
                    // comes back in place when the user lets go.
                    _inMoveSize = true;
                    RefreshVisibility();
                    break;
                case Native.EVENT_SYSTEM_MOVESIZEEND:
                    _inMoveSize = false;
                    RefreshVisibility();
                    break;
                default:
                    RefreshVisibility();
                    break;
            }
        }
        catch (Exception ex) { Fail(ex); }
    }

    void OnClaudeMoved()
    {
        if (_renderer == null || !Native.TryGetFrameBounds(_claude, out var bounds)) return;
        bool resized = _claudeW != 0 && (bounds.Width != _claudeW || bounds.Height != _claudeH);
        _claudeW = bounds.Width;
        _claudeH = bounds.Height;
        if (resized && _visible && !_inMoveSize)
        {
            // Maximize, restore or snap: Windows animates the size change. Show plain Claude
            // until it is over and a frame at the new size has arrived.
            Hide();
            _returningFromHidden = true;
            return;
        }
        if (_renderer.Width > 0 && TryGetOrigin(_claude, _renderer.Width, _renderer.Height, out int x, out int y))
            _renderer.Move(x, y);
    }

    void RefreshVisibility()
    {
        if (_claude == IntPtr.Zero || _renderer == null) return;
        bool away = !Native.IsWindowVisible(_claude) || Native.IsIconic(_claude) || Native.IsCloaked(_claude);
        if (away) _returningFromHidden = true;
        _wantVisible = _settings.Enabled && !_inMoveSize && !away;
        if (!_wantVisible && _visible) Hide();
        bool covered = _wantVisible && IsFullyCovered();
        if (covered != _covered) Trace(covered ? "covered, animation paused" : "uncovered");
        _covered = covered;
        if (_visible) KeepAboveClaude();
    }

    bool UpdateVisibility(long now)
    {
        if (_media && _visible)
        {
            // Fade out from wherever the opacity is, then leave Claude on its own.
            if (_fadeOutStart < 0)
            {
                _fadeOutStart = now;
                _fadeFrom = _opacity;
                _fadeStart = -1;
            }
            _opacity = _fadeFrom * Math.Max(0f, 1 - (now - _fadeOutStart) / (float)FadeInMs);
            if (_opacity <= 0) Hide();
            return true;
        }
        if (_fadeOutStart >= 0)
        {
            // Media went away mid fade-out: fade back in from the current opacity.
            _fadeOutStart = -1;
            _fadeStart = now - (long)(_opacity * FadeInMs);
        }
        if (!_wantVisible || _media)
        {
            _showRequestedAt = -1;
            return false;
        }
        if (!_visible)
        {
            if (_showRequestedAt < 0)
            {
                _showRequestedAt = now;
                _freshSinceRequest = false;
                // Coming back from minimized, hidden or another desktop, Windows animates the
                // real window first; the copy fades in once that animation is over.
                _showNotBefore = _returningFromHidden && Native.WindowAnimationsEnabled() ? now + SystemAnimationMs : now;
                _returningFromHidden = false;
                Trace("show requested");
            }
            bool ready = _prepared != null && now >= _showNotBefore &&
                         (_freshSinceRequest || now - _showRequestedAt >= FreshFrameWaitMs);
            if (!ready) return false;
            _showRequestedAt = -1;
            Show();
            _fadeStart = now;
            _opacity = 0;
            return true;
        }
        if (_fadeStart >= 0)
        {
            _opacity = Math.Min(1f, (now - _fadeStart) / (float)FadeInMs);
            if (_opacity >= 1)
            {
                _fadeStart = -1;
                Trace("fade-in complete");
            }
            return true;
        }
        return false;
    }

    void Show()
    {
        // Replace whatever the swap chain held from before the hide with a transparent
        // frame, so the first composited frame can never be an outdated copy.
        if (TryGetOrigin(_claude, _renderer.Width, _renderer.Height, out int x, out int y)) _renderer.Render(0, x, y);
        _visible = true;
        KeepAboveClaude(show: true);
        Trace("overlay shown");
    }

    void Hide()
    {
        Native.ShowWindow(_renderer.Window.Handle, Native.SW_HIDE);
        _visible = false;
        Trace("overlay hidden");
        _fadeStart = _fadeOutStart = -1;
        _opacity = 0;
    }

    // Keeps the overlay directly above Claude, never above other apps. Ownership normally
    // does this; activation of other windows is checked anyway.
    void KeepAboveClaude(bool show = false)
    {
        IntPtr overlay = _renderer.Window.Handle;
        IntPtr above = Native.GetWindow(_claude, Native.GW_HWNDPREV);
        if (above == overlay && !show) return;
        if (above == overlay) above = Native.GetWindow(overlay, Native.GW_HWNDPREV);
        uint flags = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | (show ? Native.SWP_SHOWWINDOW : 0);
        Native.SetWindowPos(overlay, above, 0, 0, 0, 0, flags);
        if (((long)Native.GetWindowLongPtr(overlay, Native.GWL_EXSTYLE) & Native.WS_EX_TOPMOST) != 0)
            Native.SetWindowPos(overlay, Native.HWND_NOTOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    // True when one opaque window above Claude hides it completely; the wallpaper then
    // stops animating.
    bool IsFullyCovered()
    {
        if (!Native.TryGetFrameBounds(_claude, out var c)) return false;
        IntPtr overlay = _renderer.Window.Handle;
        for (IntPtr h = Native.GetWindow(_claude, Native.GW_HWNDPREV); h != IntPtr.Zero; h = Native.GetWindow(h, Native.GW_HWNDPREV))
        {
            if (h == overlay || !Native.IsWindowVisible(h) || Native.IsIconic(h) || Native.IsCloaked(h)) continue;
            if (((long)Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE) & Native.WS_EX_TRANSPARENT) != 0) continue;
            if (!Native.TryGetFrameBounds(h, out var r)) continue;
            if (r.Left <= c.Left && r.Top <= c.Top && r.Right >= c.Right && r.Bottom >= c.Bottom) return true;
        }
        return false;
    }

    // Maps the captured frame onto the screen: the capture covers either the full window
    // rect or the DWM visible frame bounds, depending on window state.
    static bool TryGetOrigin(IntPtr claude, int w, int h, out int x, out int y)
    {
        x = y = 0;
        if (!Native.GetWindowRect(claude, out var wr)) return false;
        if (wr.Width == w && wr.Height == h) { x = wr.Left; y = wr.Top; return true; }
        if (Native.TryGetFrameBounds(claude, out var fr)) { x = fr.Left; y = fr.Top; return true; }
        x = wr.Left; y = wr.Top;
        return true;
    }

    // ---- Frames, keys and wallpaper ------------------------------------------------------

    bool TakeFrame()
    {
        long arrived = 0;
        using var frame = _capture?.TakeLatest(out arrived);
        if (frame == null) return false;
        var size = frame.ContentSize;
        if (size.Width <= 0 || size.Height <= 0) return false;
        IntPtr texture = GpuDevice.TextureOf(frame.Surface);
        try { _renderer.SetCapture(texture, size.Width, size.Height); }
        finally { Marshal.Release(texture); }

        _frameArrived = arrived;
        _haveFrame = true;
        if (_showRequestedAt >= 0) _freshSinceRequest = true;
        return true;
    }

    bool DetectKeys()
    {
        BackgroundKeys? found = null;
        bool blend = _settings.BlendSidebar;
        int current = _keys.IsSet ? _keys.Keys[0] : -1;
        var kind = FrameKind.Page;
        _renderer.ReadCapture((rows, pitch, w, h) =>
        {
            found = KeyDetector.Detect(rows, pitch, w, h, blend, current);
            if (found is { } f) kind = KeyDetector.Classify(rows, pitch, w, h, f, current);
        });
        if (found is not { } keys) return false;

        if (kind == FrameKind.Dialog)
        {
            // Everything detected before stays; the wallpaper dims with the backdrop.
            _pendingKeys = default;
            _pendingMedia = false;
            return false;
        }
        if (kind == FrameKind.Media)
        {
            // Entering media must persist across two scans, like any other change.
            _pendingKeys = default;
            if (!_media && !_pendingMedia) { _pendingMedia = true; return false; }
            _pendingMedia = false;
            if (!_media) SetMedia(true);
            return false;
        }
        _pendingMedia = false;
        if (_media) SetMedia(false);
        if (keys.SameAs(_keys))
        {
            _pendingKeys = default;
            return false;
        }
        // Other changes (theme switch, sidebar shown or hidden) must persist across two scans,
        // so a frame caught mid-transition never flashes the sidebar or the background.
        if (_keys.IsSet && !keys.SameAs(_pendingKeys))
        {
            _pendingKeys = keys;
            return false;
        }
        _pendingKeys = default;
        _keys = keys;
        _renderer.SetKeys(keys, _settings.KeyTolerance);
        Log(keys.ToString());
        return true;
    }

    void SetMedia(bool media)
    {
        _media = media;
        Log(media ? "media fills the window; showing Claude untouched" : "Claude's page is back");
    }

    bool UpdateWallpaper(long now)
    {
        if (_wall == null) return false;

        // Frames are prepared off-thread (a GIF takes a while); the previous set stays on
        // screen meanwhile, stretched to the current size.
        if (_prepTask is { IsCompleted: true })
        {
            if (_prepTask.IsCompletedSuccessfully && _prepTask.Result.Source == _wall) _prepared = _prepTask.Result;
            else if (_prepTask.IsFaulted) Log("wallpaper prepare failed: " + _prepTask.Exception?.GetBaseException().Message);
            _prepTask = null;
        }
        if (_prepTask == null && NeedsPrepare())
        {
            var wall = _wall;
            var s = _settings;
            int w = _renderer.Width, h = _renderer.Height;
            _prepTask = Task.Run(() => wall.Prepare(w, h, s.Fit, s.BlurPx));
            _prepTask.ContinueWith(_ => _wake.Set());
        }

        var prep = _prepared;
        if (prep == null) return false;
        int frames = prep.Frames.Length;
        if (_frameIndex >= frames) _frameIndex = 0;
        if (Animating && now >= _nextFrameAt)
        {
            _frameIndex = (_frameIndex + 1) % frames;
            _nextFrameAt = now + Math.Max(prep.Source.Delay(_frameIndex), 16);
        }
        if (_frameIndex == _shownFrame && prep == _shownPrepared) return false;
        _renderer.SetWallpaper(prep.Frames[_frameIndex], prep.SmallWidth, prep.SmallHeight);
        _shownFrame = _frameIndex;
        _shownPrepared = prep;
        return true;
    }

    bool NeedsPrepare()
    {
        var p = _prepared;
        var s = _settings;
        return p == null || p.Source != _wall || p.Width != _renderer.Width || p.Height != _renderer.Height ||
               p.Fit != s.Fit || p.BlurPx != s.BlurPx;
    }

    void LogStats(long now)
    {
        if (now - _statSince < 60000) return;
        if (_statRenders == 0) { _statSince = now; return; }
        double ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        Log($"stats: {_renderer?.Name} renderer, {_statRenders} renders ({_statFrames} of new Claude frames) in {(now - _statSince) / 1000}s; " +
            $"engine CPU {(_statRenders == 0 ? 0 : ms(_statWork) / _statRenders):F2} ms per render; frame arrival to present " +
            $"{(_statFrames == 0 ? 0 : ms(_statDelay) / _statFrames):F2} ms avg, {ms(_statDelayMax):F2} ms max");
        _statSince = now;
        _statRenders = _statFrames = _statDelay = _statDelayMax = _statWork = 0;
    }

    void Trace(string message)
    {
        if (_settings is { Trace: true }) Log($"trace {_clock.ElapsedMilliseconds}: {message}");
    }

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            string path = Path.Combine(Settings.Dir, "log.txt");
            if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Delete(path);
            File.AppendAllText(path, $"{DateTime.Now:O} {message}{Environment.NewLine}");
        }
        catch { }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread.Join(3000);
    }
}
