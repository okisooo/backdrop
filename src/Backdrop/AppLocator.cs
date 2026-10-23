using System.Diagnostics;

namespace Backdrop;

enum TargetApp
{
    Claude,
    Codex,
    T3Code
}

static class AppLocator
{
    static readonly object Sync = new();
    static readonly Dictionary<(uint pid, long startTicks, TargetApp app), bool> Cache = new();

    public static string DisplayName(TargetApp app) => app switch
    {
        TargetApp.Claude => "Claude",
        TargetApp.Codex => "Codex",
        TargetApp.T3Code => "T3 Code",
        _ => app.ToString()
    };

    public static bool TryParseApp(string value, out TargetApp app)
    {
        app = TargetApp.Claude;
        if (string.IsNullOrWhiteSpace(value)) return false;
        switch (value.Trim().ToLowerInvariant())
        {
            case "claude": app = TargetApp.Claude; return true;
            case "codex": app = TargetApp.Codex; return true;
            case "t3":
            case "t3code":
            case "t3-code": app = TargetApp.T3Code; return true;
            default: return false;
        }
    }

    public static TargetApp? IdentifyWindow(IntPtr h)
    {
        if (h == IntPtr.Zero || Native.ClassOf(h) != "Chrome_WidgetWin_1") return null;
        Native.GetWindowThreadProcessId(h, out uint pid);
        foreach (TargetApp candidate in new[] { TargetApp.T3Code, TargetApp.Claude, TargetApp.Codex })
            if (IsTargetProcess(pid, candidate)) return candidate;
        return null;
    }

    public static IntPtr FindMainWindow(TargetApp app)
    {
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h) || Native.ClassOf(h) != "Chrome_WidgetWin_1") return true;
            long ex = (long)Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE);
            if ((ex & Native.WS_EX_TOOLWINDOW) != 0 || Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return true;
            if (app == TargetApp.Claude && !Native.TitleOf(h).StartsWith("Claude", StringComparison.Ordinal)) return true;
            if (app == TargetApp.T3Code && string.IsNullOrWhiteSpace(Native.TitleOf(h))) return true;

            Native.GetWindowThreadProcessId(h, out uint pid);
            if (!IsTargetProcess(pid, app)) return true;

            Native.GetWindowRect(h, out var r);
            long area = (long)r.Width * r.Height;
            if (area > bestArea) { bestArea = area; best = h; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    internal static bool IsTargetProcess(uint pid, TargetApp app)
    {
        string imgPath = Native.ImagePathOf(pid) ?? "";
        long startTicks = 0;
        string modulePath = imgPath;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            try { startTicks = p.StartTime.Ticks; } catch { }
            try { modulePath = p.MainModule?.FileName ?? imgPath; } catch { }
        }
        catch
        {
            if (string.IsNullOrEmpty(imgPath)) return false;
        }

        var key = (pid, startTicks, app);
        lock (Sync)
        {
            if (startTicks != 0 && Cache.TryGetValue(key, out bool known)) return known;
        }

        bool yes = MatchesPath(modulePath, imgPath, app);
        if (startTicks != 0)
        {
            lock (Sync)
            {
                if (Cache.Count > 512) Cache.Clear();
                Cache[key] = yes;
            }
        }
        return yes;
    }

    internal static bool MatchesPath(string modulePath, string imagePath, TargetApp app)
    {
        string path = !string.IsNullOrEmpty(modulePath) ? modulePath : imagePath ?? "";
        string file = Path.GetFileName(path);
        return app switch
        {
            TargetApp.Codex => (imagePath ?? path).Contains(@"\WindowsApps\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase),
            TargetApp.Claude => file.Equals("claude.exe", StringComparison.OrdinalIgnoreCase) &&
                                (path.Contains(@"\AnthropicClaude\", StringComparison.OrdinalIgnoreCase) ||
                                 path.Contains(@"\WindowsApps\Claude_", StringComparison.OrdinalIgnoreCase)),
            TargetApp.T3Code => file.Equals("T3 Code.exe", StringComparison.OrdinalIgnoreCase) ||
                                file.Equals("T3 Code (Nightly).exe", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }
}