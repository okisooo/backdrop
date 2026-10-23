using System.Drawing;

namespace Backdrop;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        // Hides the yellow capture border on Windows 11 where the system allows it.
        try { Task.Run(WindowCapture.RequestBorderlessAsync).Wait(3000); } catch { }

        // Diagnostics: Backdrop.exe --snapshot out.png [x,y,w,h] [--window 0xHWND] [--key RRGGBB]
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

        // Started from inside a packaged app (a terminal in Codex or Claude, an installer run there):
        // it would share that app's fate, closing whenever the app updates or quits, and its
        // settings would be redirected into the package. Explorer starts it again as a program of
        // its own. The event keeps that to one attempt, should the new copy somehow carry an
        // identity too.
        if (Native.StartedFromPackagedApp())
        {
            using var escaped = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\Backdrop.Escaped", out bool fresh);
            if (fresh)
            {
                Backdrop.Log("started from inside a packaged app; relaunching on its own");
                System.Diagnostics.Process.Start("explorer.exe", $"\"{Environment.ProcessPath}\"");
                Thread.Sleep(5000); // hold the event while the new copy checks it
                return 0;
            }
        }

        using var mutex = new Mutex(true, @"Local\Backdrop.Singleton", out bool first);
        if (!first)
        {
            // Started by the watchdog: the stalled copy is being ended; take over once it has.
            if (!args.Contains("--restart")) return 0;
            try { if (!mutex.WaitOne(15000)) return 0; }
            catch (AbandonedMutexException) { }
        }

        Backdrop.Log($"started, pid {Environment.ProcessId}{(Native.StartedFromPackagedApp() ? ", from inside a packaged app" : "")}");

        // Windows starts the app again if it ends it after a hang or crash.
        Native.RegisterApplicationRestart(null, 0);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) => Backdrop.Log("ui exception: " + e.Exception);
        Application.Run(new TrayApp());
        return 0;
    }
}