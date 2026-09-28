using System.Drawing;

namespace ClaudeBackdrop;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        // Hides the yellow capture border on Windows 11 where the system allows it.
        try { Task.Run(WindowCapture.RequestBorderlessAsync).Wait(3000); } catch { }

        // Diagnostics: ClaudeBackdrop.exe --snapshot out.png [x,y,w,h] [--window 0xHWND] [--key RRGGBB]
        if (args.Length >= 2 && args[0] == "--snapshot")
        {
            Rectangle? crop = null;
            IntPtr window = IntPtr.Zero;
            int? key = null;
            for (int i = 2; i < args.Length; i++)
            {
                if (args[i] == "--window" && i + 1 < args.Length)
                    window = new IntPtr(Convert.ToInt64(args[++i].Replace("0x", ""), 16));
                else if (args[i] == "--key" && i + 1 < args.Length)
                    key = Convert.ToInt32(args[++i].TrimStart('#'), 16);
                else
                {
                    var n = args[i].Split(',').Select(int.Parse).ToArray();
                    crop = new Rectangle(n[0], n[1], n[2], n[3]);
                }
            }
            try { return Snapshot.Run(args[1], crop, window, key); }
            catch (Exception ex) { Backdrop.Log("snapshot failed: " + ex); return 1; }
        }

        if (args.Length >= 2 && args[0] == "--capture-overlay") return Snapshot.CaptureOverlay(args[1]);

        using var mutex = new Mutex(true, @"Local\ClaudeBackdrop.Singleton", out bool first);
        if (!first) return 0;

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) => Backdrop.Log("ui exception: " + e.Exception);
        Application.Run(new TrayApp());
        return 0;
    }
}