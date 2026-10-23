using System.Drawing;

namespace Backdrop;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--version")
        {
            Console.WriteLine($"Backdrop {typeof(Program).Assembly.GetName().Version?.ToString(3)}");
            return 0;
        }
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        try { Task.Run(WindowCapture.RequestBorderlessAsync).Wait(3000); } catch { }

        // Diagnostics: Backdrop.exe --snapshot out.png [x,y,w,h] [--window 0xHWND] [--key RRGGBB] [--app claude|codex|t3]
        if (args.Length >= 1 && args[0] == "--snapshot")
        {
            if (args.Length < 2) return 2;
            try
            {
                Rectangle? crop = null;
                IntPtr window = IntPtr.Zero;
                int? key = null;
                TargetApp? app = null;
                for (int i = 2; i < args.Length; i++)
                {
                    if (args[i] == "--window" && i + 1 < args.Length)
                        window = new IntPtr(Convert.ToInt64(args[++i].Replace("0x", ""), 16));
                    else if (args[i] == "--key" && i + 1 < args.Length)
                        key = Convert.ToInt32(args[++i].TrimStart('#'), 16);
                    else if (args[i] == "--app" && i + 1 < args.Length)
                    {
                        if (!AppLocator.TryParseApp(args[++i], out var parsed)) return 2;
                        app = parsed;
                    }
                    else
                    {
                        var n = args[i].Split(',').Select(int.Parse).ToArray();
                        if (n.Length != 4) return 2;
                        crop = new Rectangle(n[0], n[1], n[2], n[3]);
                    }
                }
                return Snapshot.Run(args[1], crop, window, key, app);
            }
            catch (Exception ex) { Backdrop.Log("snapshot failed: " + ex); return 1; }
        }

        if (args.Length >= 1 && args[0] == "--capture-overlay")
        {
            if (args.Length < 2) return 2;
            try
            {
                TargetApp? app = null;
                for (int i = 2; i < args.Length; i++)
                {
                    if (args[i] == "--app" && i + 1 < args.Length)
                    {
                        if (!AppLocator.TryParseApp(args[++i], out var parsed)) return 2;
                        app = parsed;
                    }
                }
                return Snapshot.CaptureOverlay(args[1], app);
            }
            catch (Exception ex) { Backdrop.Log("capture-overlay failed: " + ex); return 1; }
        }

        if (Native.StartedFromPackagedApp())
        {
            using var escaped = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\Backdrop.Escaped", out bool fresh);
            if (fresh)
            {
                Backdrop.Log("started from inside a packaged app; relaunching on its own");
                System.Diagnostics.Process.Start("explorer.exe", $"\"{Environment.ProcessPath}\"");
                Thread.Sleep(5000);
                return 0;
            }
        }

        using var mutex = new Mutex(true, @"Local\Backdrop.Singleton", out bool first);
        if (!first)
        {
            if (!args.Contains("--restart")) return 0;
            try { if (!mutex.WaitOne(15000)) return 0; }
            catch (AbandonedMutexException) { }
        }

        Backdrop.Log($"started, pid {Environment.ProcessId}{(Native.StartedFromPackagedApp() ? ", from inside a packaged app" : "")}");
        Native.RegisterApplicationRestart(null, 0);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) => Backdrop.Log("ui exception: " + e.Exception);
        Application.Run(new TrayApp());
        return 0;
    }
}
