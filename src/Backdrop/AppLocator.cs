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
    static readonly Dictionary<(uint pid, TargetApp app), bool> Cache = new();

    public static string DisplayName(TargetApp app) => app switch
    {
        TargetApp.Claude => "Claude",
        TargetApp.Codex => "Codex",
        TargetApp.T3Code => "T3 Code",
        _ => app.ToString()
    };

    public static IntPtr FindMainWindow(TargetApp app)
    {
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h) || Native.ClassOf(h) != "Chrome_WidgetWin_1") return true;
            long ex = (long)Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE);
            if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return true;
            if (app == TargetApp.Codex && Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return true;
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

    static bool IsTargetProcess(uint pid, TargetApp app)
    {
        if (Cache.TryGetValue((pid, app), out bool known)) return known;
        bool yes = false;
        try
        {
            string imgPath = Native.ImagePathOf(pid) ?? "";
            if (app == TargetApp.Codex)
            {
                yes = imgPath.Contains(@"\WindowsApps\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                using var p = Process.GetProcessById((int)pid);
                string path = p.MainModule?.FileName ?? imgPath;
                string file = Path.GetFileName(path);
                if (app == TargetApp.Claude)
                {
                    yes = file.Equals("claude.exe", StringComparison.OrdinalIgnoreCase) &&
                          (path.Contains(@"\AnthropicClaude\", StringComparison.OrdinalIgnoreCase) ||
                           path.Contains(@"\WindowsApps\Claude_", StringComparison.OrdinalIgnoreCase));
                }
                else if (app == TargetApp.T3Code)
                {
                    yes = file.StartsWith("T3 Code", StringComparison.OrdinalIgnoreCase) ||
                          (path.Contains(@"\T3Code\", StringComparison.OrdinalIgnoreCase) &&
                           file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                           !file.Contains("resource-monitor", StringComparison.OrdinalIgnoreCase));
                }
            }
        }
        catch { }
        if (Cache.Count > 512) Cache.Clear();
        Cache[(pid, app)] = yes;
        return yes;
    }
}