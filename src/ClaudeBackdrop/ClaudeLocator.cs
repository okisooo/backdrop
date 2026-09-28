using System.Diagnostics;

namespace ClaudeBackdrop;

// Finds the official Claude desktop main window. Nothing about the Claude process is modified.
static class ClaudeLocator
{
    static readonly Dictionary<uint, bool> PidIsClaude = new();

    public static IntPtr FindMainWindow()
    {
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h) || Native.ClassOf(h) != "Chrome_WidgetWin_1") return true;
            long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE);
            if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return true;
            if (!Native.TitleOf(h).StartsWith("Claude", StringComparison.Ordinal)) return true;
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (!IsClaudeProcess(pid)) return true;
            Native.GetWindowRect(h, out var r);
            long area = (long)r.Width * r.Height;
            if (area > bestArea) { bestArea = area; best = h; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    static bool IsClaudeProcess(uint pid)
    {
        if (PidIsClaude.TryGetValue(pid, out bool known)) return known;
        bool yes = false;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            string path = p.MainModule?.FileName ?? "";
            yes = Path.GetFileName(path).Equals("claude.exe", StringComparison.OrdinalIgnoreCase) &&
                  (path.Contains(@"\WindowsApps\Claude_", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains(@"\AnthropicClaude\", StringComparison.OrdinalIgnoreCase));
        }
        catch { }
        if (PidIsClaude.Count > 256) PidIsClaude.Clear();
        PidIsClaude[pid] = yes;
        return yes;
    }
}
