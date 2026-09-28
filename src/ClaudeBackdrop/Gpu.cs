using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace ClaudeBackdrop;

// Minimal helpers for calling COM interfaces through their vtables.
static unsafe class Com
{
    public static void* Vt(IntPtr obj, int index) => (*(void***)obj)[index];

    public static void Release(ref IntPtr p)
    {
        if (p == IntPtr.Zero) return;
        Marshal.Release(p);
        p = IntPtr.Zero;
    }

    public static void Check(int hr, string what)
    {
        if (hr < 0) throw new COMException(what + " failed", hr);
    }

    public static IntPtr Query(IntPtr unknown, Guid iid)
    {
        Check(Marshal.QueryInterface(unknown, in iid, out IntPtr p), "QueryInterface");
        return p;
    }
}

// The hardware Direct3D 11 device shared by capture and rendering. Its immediate context
// is only used on the engine thread.
sealed unsafe class GpuDevice : IDisposable
{
    public static readonly Guid IidTexture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    public static readonly Guid IidDxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    static readonly Guid IidDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    public const uint FormatBgra = 87;          // DXGI_FORMAT_B8G8R8A8_UNORM
    public const uint FormatR8 = 61;            // DXGI_FORMAT_R8_UNORM
    public const uint UsageDefault = 0, UsageStaging = 3;
    public const uint BindShaderResource = 0x8, BindRenderTarget = 0x20;
    const uint CpuRead = 0x20000;

    [StructLayout(LayoutKind.Sequential)]
    struct Texture2DDesc { public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags; }

    [StructLayout(LayoutKind.Sequential)]
    struct Box { public uint Left, Top, Front, Right, Bottom, Back; }

    [StructLayout(LayoutKind.Sequential)]
    struct Mapped { public byte* Data; public uint RowPitch, DepthPitch; }

    public delegate void RowsReader(byte* rows, int pitch, int width, int height);

    public IntPtr Device, Context;
    public readonly IDirect3DDevice WinRTDevice;
    IntPtr _staging;
    int _stagingW, _stagingH;

    public GpuDevice()
    {
        Com.Check(Native.D3D11CreateDevice(IntPtr.Zero, 1 /* hardware */, IntPtr.Zero, 0x20 /* BGRA support */,
            IntPtr.Zero, 0, 7, out Device, out _, out Context), "D3D11CreateDevice");
        IntPtr dxgi = Com.Query(Device, IidDxgiDevice);
        try
        {
            Com.Check(Native.CreateDirect3D11DeviceFromDXGIDevice(dxgi, out IntPtr inspectable), "CreateDirect3D11DeviceFromDXGIDevice");
            try { WinRTDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); }
            finally { Marshal.Release(inspectable); }
        }
        finally { Marshal.Release(dxgi); }
    }

    // The ID3D11Texture2D behind a captured frame's surface; the caller releases it.
    public static IntPtr TextureOf(IDirect3DSurface surface)
    {
        IntPtr access = Com.Query(((IWinRTObject)surface).NativeObject.ThisPtr, IidDxgiInterfaceAccess);
        try
        {
            Guid iid = IidTexture2D;
            IntPtr texture;
            Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Com.Vt(access, 3))(access, &iid, &texture), "GetInterface");
            return texture;
        }
        finally { Marshal.Release(access); }
    }

    public IntPtr CreateTexture(int w, int h, uint usage, uint bind, uint format = FormatBgra)
    {
        var desc = new Texture2DDesc
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = format, SampleCount = 1,
            Usage = usage, BindFlags = bind, CpuAccessFlags = usage == UsageStaging ? CpuRead : 0,
        };
        IntPtr texture;
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, Texture2DDesc*, IntPtr, IntPtr*, int>)Com.Vt(Device, 5))(Device, &desc, IntPtr.Zero, &texture), "CreateTexture2D");
        return texture;
    }

    public IntPtr CreateView(IntPtr texture)
    {
        IntPtr view;
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr*, int>)Com.Vt(Device, 7))(Device, texture, IntPtr.Zero, &view), "CreateShaderResourceView");
        return view;
    }

    public IntPtr CreateTargetView(IntPtr texture)
    {
        IntPtr view;
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr*, int>)Com.Vt(Device, 9))(Device, texture, IntPtr.Zero, &view), "CreateRenderTargetView");
        return view;
    }

    // Copies the top-left w x h of `src` into `dst`.
    public void CopyRegion(IntPtr dst, IntPtr src, int w, int h)
    {
        var box = new Box { Right = (uint)w, Bottom = (uint)h, Back = 1 };
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, Box*, void>)Com.Vt(Context, 46))(
            Context, dst, 0, 0, 0, 0, src, 0, &box);
    }

    public void Upload(IntPtr texture, void* pixels, int rowPitch)
    {
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, IntPtr, void*, uint, uint, void>)Com.Vt(Context, 48))(
            Context, texture, 0, IntPtr.Zero, pixels, (uint)rowPitch, 0);
    }

    // Copies the top-left w x h of `texture` to the CPU and hands its rows to `reader`.
    public void Read(IntPtr texture, int w, int h, RowsReader reader)
    {
        if (_staging == IntPtr.Zero || _stagingW != w || _stagingH != h)
        {
            Com.Release(ref _staging);
            _staging = CreateTexture(w, h, UsageStaging, 0);
            _stagingW = w; _stagingH = h;
        }
        CopyRegion(_staging, texture, w, h);
        Mapped m;
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, Mapped*, int>)Com.Vt(Context, 14))(
            Context, _staging, 0, 1 /* D3D11_MAP_READ */, 0, &m), "Map");
        try { reader(m.Data, (int)m.RowPitch, w, h); }
        finally { ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Com.Vt(Context, 15))(Context, _staging, 0); }
    }

    public void Dispose()
    {
        Com.Release(ref _staging);
        try { WinRTDevice?.Dispose(); } catch { }
        Com.Release(ref Context);
        Com.Release(ref Device);
    }
}
