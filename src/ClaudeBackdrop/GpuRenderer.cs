using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeBackdrop;

interface IRenderer : IDisposable
{
    string Name { get; }
    OverlayWindow Window { get; }
    int Width { get; }
    int Height { get; }

    // A new captured frame: `texture` lives on the shared device, content is w x h.
    void SetCapture(IntPtr texture, int w, int h);
    // CPU access to the current capture, for background-color detection.
    void ReadCapture(GpuDevice.RowsReader reader);
    void SetKeys(BackgroundKeys keys, int tolerance);
    // How strongly the wallpaper calms toward Claude's background behind content (0..1).
    void SetCalm(double strength);
    void SetWash(double amount);
    void SetWallpaper(uint[] pixels, int sw, int sh);
    // Draws with the given overlay opacity (0..1) and shows the result at screen (x, y).
    void Render(float opacity, int x, int y);
    void Move(int x, int y);
}

// Composites on the GPU into a DirectComposition swap chain shown by the overlay window.
// The capture never leaves video memory, so a frame costs almost no CPU.
sealed unsafe class GpuRenderer : IRenderer
{
    // Claude's UI is ink drawn over its flat background color B. Each pixel is read as ink
    // at the smallest opacity that reproduces it ("color to alpha") and laid over the
    // wallpaper W instead: P + (1 - a) * (W - B). This is exact for every pixel on its own,
    // so text, rounded corners, shadows and dialog backdrops come out as Claude drew them.
    //
    // Claude's translucent panels (message bubbles, chips, code blocks) were designed to be
    // read against a flat background. To keep them legible, the wallpaper calms toward B
    // behind content: the ink density of the frame is measured at quarter resolution and
    // blurred wide, and that smooth field sets how much of B stays behind each pixel. A
    // field this smooth has no edges of its own, so it cannot misplace a panel or cut one.
    const string ShaderSource = """
        cbuffer Params : register(b0)
        {
            float4 key0;
            float4 key1;
            float4 key2;
            float keyCount;
            float tolerance;
            float solidRight;
            float opacity;
            float2 size;
            float calm;
            float debugView;
            float4 pane;   // x0, y0, x1, y1 of a docked pane passed through unchanged
            float wash;    // blend of the wallpaper toward Claude's background
        };

        Texture2D<float4> captured : register(t0);
        Texture2D<float4> wallpaper : register(t1);
        Texture2D<float> field : register(t2);
        SamplerState linearClamp : register(s0);

        float4 VS(uint id : SV_VertexID) : SV_Position
        {
            float2 t = float2((id << 1) & 2, id & 2);
            return float4(t * float2(2, -2) + float2(-1, 1), 0, 1);
        }

        // Smallest opacity of an ink that turns background k into p.
        float InkAlpha(float3 p, float3 k)
        {
            float3 d = p - k;
            float3 lighter = d / max(1 - k, 1e-5);
            float3 darker = -d / max(k, 1e-5);
            float3 a = d > 0 ? lighter : darker;
            a = abs(d) <= tolerance ? 0 : a;
            return saturate(max(a.r, max(a.g, a.b)));
        }

        // Ink opacity against the nearest background key; k receives that key.
        float KeyAlpha(float3 p, out float3 k)
        {
            k = key0.rgb;
            float a = InkAlpha(p, k);
            if (keyCount > 1.5) { float a1 = InkAlpha(p, key1.rgb); if (a1 < a) { a = a1; k = key1.rgb; } }
            if (keyCount > 2.5) { float a2 = InkAlpha(p, key2.rgb); if (a2 < a) { a = a2; k = key2.rgb; } }
            return a;
        }

        float2 FieldSize() { return ceil(size / 4); }

        // The sidebar and a docked pane of foreign content are shown exactly as captured.
        bool Solid(float2 xy)
        {
            return xy.x < solidRight || (xy.x >= pane.x && xy.x < pane.z && xy.y >= pane.y && xy.y < pane.w);
        }

        // Pass 1. Detail density at quarter resolution: the ink contrast inside each 4x4 block,
        // relative to the block's own backing. Text and icons score high. Flat areas score zero
        // whatever their shade, so a dialog backdrop dims the wallpaper instead of calming it,
        // and soft shadows stay on the wallpaper. Opaque panels have no backing to calm, so
        // their contents score zero and do not spread a halo around menus or the composer.
        // The solid sidebar counts as empty, so it does not calm the chat next to it.
        float PSDensity(float4 pos : SV_Position) : SV_Target
        {
            int2 q = int2(pos.xy) * 4;
            float lo = 1, hi = 0;
            [unroll] for (int j = 0; j < 4; j++)
                [unroll] for (int i = 0; i < 4; i++)
                {
                    int2 t = min(q + int2(i, j), int2(size) - 1);
                    float4 p = captured.Load(int3(t, 0));
                    float3 k = key0.rgb;
                    float a = KeyAlpha(p.rgb, k);
                    bool counted = !Solid(float2(t)) && p.a >= 0.999;
                    lo = counted ? min(lo, a) : lo;
                    hi = counted ? max(hi, a) : hi;
                }
            float backing = 1 - lo;
            return backing > 0.05 ? saturate((hi - lo) / backing) * smoothstep(0.05, 0.3, backing) : 0;
        }

        // Pass 2. Wide Gaussian blur of the density (sigma 6 quarter-res texels, 24 px).
        float Blur(int2 q, int2 direction)
        {
            int2 last = int2(FieldSize()) - 1;
            float sum = 0, weight = 0;
            [unroll] for (int i = -12; i <= 12; i++)
            {
                float w = exp(-(i * i) / 72.0);
                sum += w * field.Load(int3(clamp(q + direction * i, int2(0, 0), last), 0));
                weight += w;
            }
            return sum / weight;
        }

        float PSBlurX(float4 pos : SV_Position) : SV_Target { return Blur(int2(pos.xy), int2(1, 0)); }
        float PSBlurY(float4 pos : SV_Position) : SV_Target { return Blur(int2(pos.xy), int2(0, 1)); }

        // Pass 3. Composite over the wallpaper, kept closer to Claude's background where the
        // smooth density field says there is content.
        float4 PS(float4 pos : SV_Position) : SV_Target
        {
            int2 q = int2(pos.xy);
            float4 p = captured.Load(int3(q, 0));
            float density = calm > 0 ? field.SampleLevel(linearClamp, pos.xy / (4 * FieldSize()), 0) : 0;
            if (debugView > 0.5) return float4(saturate(density * 2).xxx, 1);
            if (Solid(pos.xy) || p.a < 0.999)
                return p * opacity;
            float3 k = key0.rgb;
            float a = KeyAlpha(p.rgb, k);
            float keep = calm * smoothstep(0.03, 0.3, density);
            float3 w = lerp(wallpaper.SampleLevel(linearClamp, pos.xy / size, 0).rgb, key0.rgb, wash);
            return float4(saturate(p.rgb + (1 - a) * (1 - keep) * (w - k)), 1) * opacity;
        }
        """;

    [StructLayout(LayoutKind.Sequential)]
    struct Params
    {
        public Vector4 Key0, Key1, Key2;
        public float KeyCount, Tolerance, SolidRight, Opacity;
        public float Width, Height, Calm, DebugView;
        public Vector4 Pane;
        public float Wash, Pad0, Pad1, Pad2;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SwapChainDesc1 { public uint Width, Height, Format, Stereo, SampleCount, SampleQuality, BufferUsage, BufferCount, Scaling, SwapEffect, AlphaMode, Flags; }

    [StructLayout(LayoutKind.Sequential)]
    struct Viewport { public float X, Y, W, H, MinDepth, MaxDepth; }

    [StructLayout(LayoutKind.Sequential)]
    struct SamplerDesc
    {
        public uint Filter, AddressU, AddressV, AddressW;
        public float MipLodBias;
        public uint MaxAnisotropy, ComparisonFunc;
        public float Border0, Border1, Border2, Border3, MinLod, MaxLod;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BufferDesc { public uint ByteWidth, Usage, BindFlags, CpuAccessFlags, MiscFlags, StructureByteStride; }

    // A texture that one pass renders into and a later one reads.
    struct Stage
    {
        public IntPtr Texture, View, Target;
        public void Release() { Com.Release(ref Target); Com.Release(ref View); Com.Release(ref Texture); }
    }

    static readonly Guid IidDxgiFactory2 = new("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
    static readonly Guid IidDCompositionDevice = new("C37EA93A-E7AA-450D-B16F-9746CB0407F3");
    const uint FormatR16Float = 54; // DXGI_FORMAT_R16_FLOAT

    readonly GpuDevice _gpu;
    IntPtr _factory, _dcomp, _target, _visual, _swapChain, _targetView;
    IntPtr _vs, _psDensity, _psBlurX, _psBlurY, _ps, _sampler, _constants;
    IntPtr _capture, _captureView, _wallpaper, _wallpaperView;
    Stage _fieldA, _fieldB; // quarter resolution
    int _capW, _capH, _wallW, _wallH, _chainW, _chainH;
    int _winX = int.MinValue, _winY, _winW, _winH;
    Params _params;

    public string Name => "GPU";
    public OverlayWindow Window { get; }
    public int Width => _capW;
    public int Height => _capH;

    public GpuRenderer(GpuDevice gpu)
    {
        _gpu = gpu;
        Window = new OverlayWindow(composition: true);
        try
        {
            IntPtr dxgiDevice = Com.Query(gpu.Device, GpuDevice.IidDxgiDevice);
            try
            {
                IntPtr adapter;
                Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Com.Vt(dxgiDevice, 7))(dxgiDevice, &adapter), "GetAdapter");
                try
                {
                    Guid iid = IidDxgiFactory2;
                    IntPtr factory;
                    Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Com.Vt(adapter, 6))(adapter, &iid, &factory), "GetParent");
                    _factory = factory;
                }
                finally { Marshal.Release(adapter); }

                Guid dcompIid = IidDCompositionDevice;
                Com.Check(Native.DCompositionCreateDevice(dxgiDevice, &dcompIid, out _dcomp), "DCompositionCreateDevice");
            }
            finally { Marshal.Release(dxgiDevice); }

            IntPtr target, visual;
            Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, IntPtr*, int>)Com.Vt(_dcomp, 6))(_dcomp, Window.Handle, 1, &target), "CreateTargetForHwnd");
            _target = target;
            Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Com.Vt(_dcomp, 7))(_dcomp, &visual), "CreateVisual");
            _visual = visual;
            Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Com.Vt(_target, 3))(_target, _visual), "SetRoot");

            _vs = CompileShader("VS", "vs_5_0", 12);
            _psDensity = CompileShader("PSDensity", "ps_5_0", 15);
            _psBlurX = CompileShader("PSBlurX", "ps_5_0", 15);
            _psBlurY = CompileShader("PSBlurY", "ps_5_0", 15);
            _ps = CompileShader("PS", "ps_5_0", 15);

            var sampler = new SamplerDesc { Filter = 0x15 /* linear */, AddressU = 3, AddressV = 3, AddressW = 3 /* clamp */, MaxAnisotropy = 1, ComparisonFunc = 1, MaxLod = float.MaxValue };
            IntPtr s;
            Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, SamplerDesc*, IntPtr*, int>)Com.Vt(gpu.Device, 23))(gpu.Device, &sampler, &s), "CreateSamplerState");
            _sampler = s;

            var buffer = new BufferDesc { ByteWidth = (uint)sizeof(Params), BindFlags = 0x4 /* constant buffer */ };
            IntPtr b;
            Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, BufferDesc*, IntPtr, IntPtr*, int>)Com.Vt(gpu.Device, 3))(gpu.Device, &buffer, IntPtr.Zero, &b), "CreateBuffer");
            _constants = b;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    IntPtr CompileShader(string entry, string profile, int createMethod)
    {
        byte[] source = Encoding.ASCII.GetBytes(ShaderSource);
        IntPtr code, errors;
        int hr;
        fixed (byte* p = source)
            hr = Native.D3DCompile(p, (nuint)source.Length, "backdrop.hlsl", IntPtr.Zero, IntPtr.Zero, entry, profile,
                1u << 15 /* optimization level 3 */, 0, out code, out errors);
        string message = errors != IntPtr.Zero ? Marshal.PtrToStringAnsi((IntPtr)BlobPointer(errors)) : null;
        Com.Release(ref errors);
        if (hr < 0) throw new COMException("shader compile failed: " + message, hr);
        try
        {
            IntPtr shader;
            Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, void*, nuint, IntPtr, IntPtr*, int>)Com.Vt(_gpu.Device, createMethod))(
                _gpu.Device, BlobPointer(code), BlobSize(code), IntPtr.Zero, &shader), "CreateShader");
            return shader;
        }
        finally { Marshal.Release(code); }
    }

    static void* BlobPointer(IntPtr blob) => ((delegate* unmanaged[Stdcall]<IntPtr, void*>)Com.Vt(blob, 3))(blob);
    static nuint BlobSize(IntPtr blob) => ((delegate* unmanaged[Stdcall]<IntPtr, nuint>)Com.Vt(blob, 4))(blob);

    public void SetCapture(IntPtr texture, int w, int h)
    {
        if (_capture == IntPtr.Zero || _capW != w || _capH != h)
        {
            Com.Release(ref _captureView);
            Com.Release(ref _capture);
            _fieldA.Release();
            _fieldB.Release();
            _capture = _gpu.CreateTexture(w, h, GpuDevice.UsageDefault, GpuDevice.BindShaderResource);
            _captureView = _gpu.CreateView(_capture);
            _fieldA = CreateStage((w + 3) / 4, (h + 3) / 4, FormatR16Float);
            _fieldB = CreateStage((w + 3) / 4, (h + 3) / 4, FormatR16Float);
            _capW = w; _capH = h;
        }
        _gpu.CopyRegion(_capture, texture, w, h);
    }

    Stage CreateStage(int w, int h, uint format)
    {
        var stage = new Stage { Texture = _gpu.CreateTexture(w, h, GpuDevice.UsageDefault, GpuDevice.BindShaderResource | GpuDevice.BindRenderTarget, format) };
        stage.View = _gpu.CreateView(stage.Texture);
        stage.Target = _gpu.CreateTargetView(stage.Texture);
        return stage;
    }

    public void ReadCapture(GpuDevice.RowsReader reader)
    {
        if (_capture != IntPtr.Zero) _gpu.Read(_capture, _capW, _capH, reader);
    }

    public void SetKeys(BackgroundKeys keys, int tolerance)
    {
        int[] k = keys.Keys;
        Vector4 Color(int i)
        {
            int c = k[Math.Min(i, k.Length - 1)];
            return new Vector4(((c >> 16) & 255) / 255f, ((c >> 8) & 255) / 255f, (c & 255) / 255f, 1);
        }
        _params.Key0 = Color(0);
        _params.Key1 = Color(1);
        _params.Key2 = Color(2);
        _params.KeyCount = k.Length;
        _params.Tolerance = (tolerance + 0.5f) / 255f;
        _params.SolidRight = keys.SolidRight;
        _params.Pane = new Vector4(keys.Pane.X0, keys.Pane.Y0, keys.Pane.X1, keys.Pane.Y1);
    }

    public void SetCalm(double strength) => _params.Calm = (float)Math.Clamp(strength, 0, 1);

    public void SetWash(double amount) => _params.Wash = (float)Math.Clamp(amount, 0, 1);

    public void SetWallpaper(uint[] pixels, int sw, int sh)
    {
        if (_wallpaper == IntPtr.Zero || _wallW != sw || _wallH != sh)
        {
            Com.Release(ref _wallpaperView);
            Com.Release(ref _wallpaper);
            _wallpaper = _gpu.CreateTexture(sw, sh, GpuDevice.UsageDefault, GpuDevice.BindShaderResource);
            _wallpaperView = _gpu.CreateView(_wallpaper);
            _wallW = sw; _wallH = sh;
        }
        fixed (uint* p = pixels) _gpu.Upload(_wallpaper, p, sw * 4);
    }

    void EnsureSwapChain(int w, int h)
    {
        if (_swapChain != IntPtr.Zero && _chainW == w && _chainH == h) return;
        Com.Release(ref _targetView);
        if (_swapChain == IntPtr.Zero)
        {
            var desc = new SwapChainDesc1
            {
                Width = (uint)w, Height = (uint)h, Format = GpuDevice.FormatBgra, SampleCount = 1,
                BufferUsage = 0x20 /* render target output */, BufferCount = 2, Scaling = 0 /* stretch */,
                SwapEffect = 3 /* flip sequential */, AlphaMode = 1 /* premultiplied */,
            };
            IntPtr chain;
            Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, SwapChainDesc1*, IntPtr, IntPtr*, int>)Com.Vt(_factory, 24))(
                _factory, _gpu.Device, &desc, IntPtr.Zero, &chain), "CreateSwapChainForComposition");
            _swapChain = chain;
            Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Com.Vt(_visual, 15))(_visual, _swapChain), "SetContent");
            Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)Com.Vt(_dcomp, 3))(_dcomp), "Commit");
        }
        else
        {
            Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, uint, int>)Com.Vt(_swapChain, 13))(
                _swapChain, 0, (uint)w, (uint)h, 0, 0), "ResizeBuffers");
        }
        Guid iid = GpuDevice.IidTexture2D;
        IntPtr back;
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, Guid*, IntPtr*, int>)Com.Vt(_swapChain, 9))(_swapChain, 0, &iid, &back), "GetBuffer");
        try { _targetView = _gpu.CreateTargetView(back); }
        finally { Marshal.Release(back); }
        _chainW = w; _chainH = h;
    }

    // Runs the passes into `targetView`.
    void Draw(IntPtr targetView, int w, int h, float opacity, bool debugView = false)
    {
        IntPtr ctx = _gpu.Context;
        _params.Opacity = opacity;
        _params.Width = w;
        _params.Height = h;
        _params.DebugView = debugView ? 1 : 0;
        fixed (Params* p = &_params)
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, IntPtr, void*, uint, uint, void>)Com.Vt(ctx, 48))(ctx, _constants, 0, IntPtr.Zero, p, 0, 0);

        ((delegate* unmanaged[Stdcall]<IntPtr, uint, void>)Com.Vt(ctx, 24))(ctx, 4 /* triangle list */);
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, uint, void>)Com.Vt(ctx, 11))(ctx, _vs, IntPtr.Zero, 0);
        IntPtr sampler = _sampler, constants = _constants;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)Com.Vt(ctx, 10))(ctx, 0, 1, &sampler);
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)Com.Vt(ctx, 16))(ctx, 0, 1, &constants);

        IntPtr none = IntPtr.Zero;
        if (_params.Calm > 0)
        {
            int fw = (w + 3) / 4, fh = (h + 3) / 4;
            Pass(_fieldA.Target, _psDensity, fw, fh, _captureView, none, none);
            Pass(_fieldB.Target, _psBlurX, fw, fh, none, none, _fieldA.View);
            Pass(_fieldA.Target, _psBlurY, fw, fh, none, none, _fieldB.View);
        }
        Pass(targetView, _ps, w, h, _captureView, _wallpaperView, _params.Calm > 0 ? _fieldA.View : none);
        Unbind();
    }

    void Pass(IntPtr targetView, IntPtr shader, int w, int h, IntPtr t0, IntPtr t1, IntPtr t2)
    {
        IntPtr ctx = _gpu.Context;
        Unbind();
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, IntPtr, void>)Com.Vt(ctx, 33))(ctx, 1, &targetView, IntPtr.Zero);
        var viewport = new Viewport { W = w, H = h, MaxDepth = 1 };
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, Viewport*, void>)Com.Vt(ctx, 44))(ctx, 1, &viewport);
        IntPtr* views = stackalloc IntPtr[3];
        views[0] = t0; views[1] = t1; views[2] = t2;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)Com.Vt(ctx, 8))(ctx, 0, 3, views);
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, uint, void>)Com.Vt(ctx, 9))(ctx, shader, IntPtr.Zero, 0);
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, void>)Com.Vt(ctx, 13))(ctx, 3, 0);
    }

    // Clears shader inputs and the render target, so a texture is never bound as both.
    void Unbind()
    {
        IntPtr ctx = _gpu.Context;
        IntPtr* none = stackalloc IntPtr[3];
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)Com.Vt(ctx, 8))(ctx, 0, 3, none);
        IntPtr noTarget = IntPtr.Zero;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, IntPtr, void>)Com.Vt(ctx, 33))(ctx, 1, &noTarget, IntPtr.Zero);
    }

    public void Render(float opacity, int x, int y)
    {
        if (_capture == IntPtr.Zero || _wallpaper == IntPtr.Zero) return;
        int w = _capW, h = _capH;
        EnsureSwapChain(w, h);
        Draw(_targetView, w, h, opacity);
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int>)Com.Vt(_swapChain, 8))(_swapChain, 1, 0), "Present");
        Place(x, y, w, h);
    }

    // Diagnostics: renders into an offscreen texture and reads the pixels back. With
    // debugView, the output is the calm field (brighter = more of Claude's background kept).
    public void RenderOffscreen(float opacity, bool debugView, GpuDevice.RowsReader reader)
    {
        int w = _capW, h = _capH;
        IntPtr texture = _gpu.CreateTexture(w, h, GpuDevice.UsageDefault, GpuDevice.BindRenderTarget);
        IntPtr view = IntPtr.Zero;
        try
        {
            view = _gpu.CreateTargetView(texture);
            Draw(view, w, h, opacity, debugView);
            _gpu.Read(texture, w, h, reader);
        }
        finally
        {
            Com.Release(ref view);
            Com.Release(ref texture);
        }
    }

    public void Move(int x, int y)
    {
        if (_winX != int.MinValue) Place(x, y, _winW, _winH);
    }

    void Place(int x, int y, int w, int h)
    {
        if (x == _winX && y == _winY && w == _winW && h == _winH) return;
        Native.SetWindowPos(Window.Handle, IntPtr.Zero, x, y, w, h, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        _winX = x; _winY = y; _winW = w; _winH = h;
    }

    public void Dispose()
    {
        Com.Release(ref _targetView);
        Com.Release(ref _swapChain);
        _fieldA.Release();
        _fieldB.Release();
        Com.Release(ref _wallpaperView);
        Com.Release(ref _wallpaper);
        Com.Release(ref _captureView);
        Com.Release(ref _capture);
        Com.Release(ref _constants);
        Com.Release(ref _sampler);
        Com.Release(ref _ps);
        Com.Release(ref _psBlurY);
        Com.Release(ref _psBlurX);
        Com.Release(ref _psDensity);
        Com.Release(ref _vs);
        Com.Release(ref _visual);
        Com.Release(ref _target);
        Com.Release(ref _dcomp);
        Com.Release(ref _factory);
        Window.Dispose();
    }
}
