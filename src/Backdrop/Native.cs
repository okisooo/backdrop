using System.Runtime.InteropServices;
using System.Text;

namespace Backdrop;

static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    public delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    public const int GWLP_HWNDPARENT = -8;
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOPMOST = 0x8;
    public const int WS_EX_TRANSPARENT = 0x20;
    public const int WS_EX_TOOLWINDOW = 0x80;
    public const int WS_EX_LAYERED = 0x80000;
    public const int WS_EX_NOREDIRECTIONBITMAP = 0x200000;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const uint GW_HWNDPREV = 3, GW_OWNER = 4;
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    public const int SW_HIDE = 0;
    public const int WM_SHOWWINDOW = 0x18, WM_POWERBROADCAST = 0x218;
    public const int SW_PARENTOPENING = 3;
    public const int PBT_APMRESUMEAUTOMATIC = 0x12;
    public const uint LWA_ALPHA = 2;

    public const uint EVENT_SYSTEM_FOREGROUND = 0x3;
    public const uint EVENT_SYSTEM_MOVESIZESTART = 0xA, EVENT_SYSTEM_MOVESIZEEND = 0xB;
    public const uint EVENT_SYSTEM_MINIMIZESTART = 0x16, EVENT_SYSTEM_MINIMIZEEND = 0x17;
    public const uint EVENT_OBJECT_DESTROY = 0x8001, EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_HIDE = 0x8003, EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    public const uint EVENT_OBJECT_CLOAKED = 0x8017, EVENT_OBJECT_UNCLOAKED = 0x8018;
    public const uint WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;
    public const uint QS_ALLINPUT = 0x04FF, MWMO_INPUTAVAILABLE = 0x4, PM_REMOVE = 1;

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsHungAppWindow(IntPtr h);

    // A process started from inside a packaged (MSIX) app, such as a terminal in Codex, runs in
    // that app's container: its AppData writes are redirected into the package and Windows
    // closes it with the app. It carries no package identity of its own, so the test is
    // whether any process it descends from runs from WindowsApps.
    [StructLayout(LayoutKind.Sequential)]
    struct ProcessBasicInformation { public IntPtr Reserved1, PebBaseAddress, Reserved2a, Reserved2b, UniqueProcessId, InheritedFromUniqueProcessId; }
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr process, int infoClass, out ProcessBasicInformation info, int size, out int returned);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, int flags, System.Text.StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr process, out long created, out long exited, out long kernel, out long user);

    // Full path of a process's executable, or null when it cannot be read.
    public static string ImagePathOf(uint pid)
    {
        IntPtr h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, (int)pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var name = new StringBuilder(1024);
            int size = name.Capacity;
            return QueryFullProcessImageName(h, 0, name, ref size) ? name.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    public static bool StartedFromPackagedApp()
    {
        int pid = Environment.ProcessId;
        long childCreated = long.MaxValue;
        for (int depth = 0; depth < 10; depth++)
        {
            IntPtr h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                // A parent id whose process started after its child is a reused id: stop there.
                if (GetProcessTimes(h, out long created, out _, out _, out _) && created > childCreated) return false;
                childCreated = created;
                if (depth > 0)
                {
                    var name = new System.Text.StringBuilder(1024);
                    int size = name.Capacity;
                    if (QueryFullProcessImageName(h, 0, name, ref size) &&
                        name.ToString().Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)) return true;
                }
                if (NtQueryInformationProcess(h, 0, out var info, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0) return false;
                pid = (int)info.InheritedFromUniqueProcessId;
                if (pid <= 4) return false;
            }
            finally { CloseHandle(h); }
        }
        return false;
    }
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll")] public static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")]
    public static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT pos, ref SIZE size, IntPtr src,
        ref POINT srcPos, uint key, ref BLENDFUNCTION blend, uint flags);
    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc cb, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")]
    public static extern uint MsgWaitForMultipleObjectsEx(uint count, IntPtr[] handles, uint milliseconds, uint wakeMask, uint flags);
    [StructLayout(LayoutKind.Sequential)] public struct ANIMATIONINFO { public int cbSize, iMinAnimate; }
    [DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint action, uint param, ref ANIMATIONINFO info, uint winIni);

    // Whether Windows animates minimize/restore (and with it window show/hide transitions).
    public static bool WindowAnimationsEnabled()
    {
        var info = new ANIMATIONINFO { cbSize = 8 };
        return !SystemParametersInfo(0x48 /* SPI_GETANIMATION */, 8, ref info, 0) || info.iMinAnimate != 0;
    }

    // Lets Windows start the app again when it ends it after a hang or crash.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern int RegisterApplicationRestart(string commandLine, int flags);

    [DllImport("user32.dll")] public static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);

    [DllImport("d3d11.dll")]
    public static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
        IntPtr featureLevels, uint levels, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);
    [DllImport("d3d11.dll")] public static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
    [DllImport("dcomp.dll")] public static extern unsafe int DCompositionCreateDevice(IntPtr dxgiDevice, Guid* iid, out IntPtr device);
    [DllImport("d3dcompiler_47.dll")]
    public static extern unsafe int D3DCompile(void* source, nuint size, [MarshalAs(UnmanagedType.LPStr)] string sourceName,
        IntPtr defines, IntPtr include, [MarshalAs(UnmanagedType.LPStr)] string entryPoint, [MarshalAs(UnmanagedType.LPStr)] string target,
        uint flags1, uint flags2, out IntPtr code, out IntPtr errors);

    public static string ClassOf(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, s.Capacity); return s.ToString(); }
    public static string TitleOf(IntPtr h) { var s = new StringBuilder(256); GetWindowText(h, s, s.Capacity); return s.ToString(); }

    public static bool IsCloaked(IntPtr h) => DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int c, 4) == 0 && c != 0;

    public static bool TryGetFrameBounds(IntPtr h, out RECT r) => DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out r, 16) == 0;
}
