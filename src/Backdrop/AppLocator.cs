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
    static readonly Dictionary<(uint pid, TargetApp app), bool> Cache = new();
    static long _cacheClearedAt = Environment.TickCount64;

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
        long now = Environment.TickCount64;
        lock (Sync)
        {
            if (now - _cacheClearedAt > 30_000 || Cache.Count > 512)
            {
                Cache.Clear();
                _cacheClearedAt = now;
            }
            if (Cache.TryGetValue((pid, app), out bool known)) return known;
        }

        string path = Native.ImagePathOf(pid);
        if (string.IsNullOrEmpty(path)) return false;

        bool yes = MatchesPath(path, app);
        lock (Sync)
        {
            Cache[(pid, app)] = yes;
        }
        return yes;
    }

    internal static bool MatchesPath(string path, TargetApp app)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string file = Path.GetFileName(path);
        return app switch
        {
            TargetApp.Codex => path.Contains(@"\WindowsApps\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase),
            TargetApp.Claude => file.Equals("claude.exe", StringComparison.OrdinalIgnoreCase) &&
                                (path.Contains(@"\AnthropicClaude\", StringComparison.OrdinalIgnoreCase) ||
                                 path.Contains(@"\WindowsApps\Claude_", StringComparison.OrdinalIgnoreCase)),
            TargetApp.T3Code => file.Equals("T3 Code.exe", StringComparison.OrdinalIgnoreCase) ||
                                file.Equals("T3 Code (Nightly).exe", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }
}