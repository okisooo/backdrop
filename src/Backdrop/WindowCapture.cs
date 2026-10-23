using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Backdrop;

[ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IGraphicsCaptureItemInterop
{
    IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
    IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
}

// Windows.Graphics.Capture of one window. Only the newest frame is kept; the engine takes
// it when it is ready for another, so bursts of frames coalesce by themselves.
sealed class WindowCapture : IDisposable
{
    const DirectXPixelFormat Format = DirectXPixelFormat.B8G8R8A8UIntNormalized;

    readonly IDirect3DDevice _device;
    readonly GraphicsCaptureItem _item;
    readonly Direct3D11CaptureFramePool _pool;
    readonly GraphicsCaptureSession _session;
    readonly Action _onChange;
    readonly object _gate = new();
    Direct3D11CaptureFrame _latest;
    long _latestArrived;
    SizeInt32 _poolSize;
    bool _disposed;
    Exception _failure;
    public Exception Failure => Volatile.Read(ref _failure);

    public bool Closed { get; private set; }

    public WindowCapture(IntPtr hwnd, IDirect3DDevice device, Action onChange)
    {
        _device = device;
        _onChange = onChange;
        _item = CreateItem(hwnd);
        _item.Closed += OnClosed;
        _poolSize = _item.Size;
        // Three buffers: one being rendered, the newest waiting, one being filled.
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, Format, 3, _poolSize);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(_item);
        try { _session.IsCursorCaptureEnabled = false; } catch { }
        try { _session.IsBorderRequired = false; } catch { }
        _session.StartCapture();
    }

    public static Task RequestBorderlessAsync()
    {
        try { return GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).AsTask(); }
        catch { return Task.CompletedTask; }
    }

    void OnClosed(GraphicsCaptureItem sender, object args)
    {
        Closed = true;
        _onChange();
    }

    void OnFrameArrived(Direct3D11CaptureFramePool pool, object args)
    {
        Direct3D11CaptureFrame frame = null;
        try
        {
            frame = pool.TryGetNextFrame();
            if (frame == null) return;
            long arrived = System.Diagnostics.Stopwatch.GetTimestamp();
            var size = frame.ContentSize;
            if (size.Width <= 0 || size.Height <= 0) return;
            if (size.Width != _poolSize.Width || size.Height != _poolSize.Height)
            {
                pool.Recreate(_device, Format, 3, size);
                _poolSize = size;
            }
            lock (_gate)
            {
                if (_disposed) return;
                _latest?.Dispose();
                _latest = frame;
                frame = null; // Ownership passes to TakeLatest/Dispose.
                _latestArrived = arrived;
            }
            _onChange();
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (_disposed) return;
                Interlocked.CompareExchange(ref _failure, ex, null);
            }
            // Wake the engine so its existing device-rebuild path handles the failure.
            _onChange();
        }
        finally { frame?.Dispose(); }
    }

    // The newest unprocessed frame, or null. The caller disposes it. `arrived` is the
    // Stopwatch timestamp of its arrival.
    public Direct3D11CaptureFrame TakeLatest(out long arrived)
    {
        lock (_gate)
        {
            var frame = _latest;
            arrived = _latestArrived;
            _latest = null;
            return frame;
        }
    }

    public Direct3D11CaptureFrame TakeLatest() => TakeLatest(out _);

    static GraphicsCaptureItem CreateItem(IntPtr hwnd)
    {
        var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interop = factory.AsInterface<IGraphicsCaptureItemInterop>();
        var iid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        IntPtr ptr = interop.CreateForWindow(hwnd, ref iid);
        try { return GraphicsCaptureItem.FromAbi(ptr); }
        finally { Marshal.Release(ptr); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _latest?.Dispose();
            _latest = null;
        }
        try { _pool.FrameArrived -= OnFrameArrived; } catch { }
        try { _item.Closed -= OnClosed; } catch { }
        try { _session.Dispose(); } catch { }
        try { _pool.Dispose(); } catch { }
    }
}
