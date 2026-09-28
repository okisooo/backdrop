namespace CodexBackdrop;

static class Program
{
    [STAThread]
    static int Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        using var mutex = new Mutex(true, @"Local\CodexBackdrop.Singleton", out bool first);
        if (!first) return 0;

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) => BackdropLog.Append(AppPaths.Create().LogPath, "ui exception: " + e.Exception);
        Application.Run(new TrayApp());
        return 0;
    }
}
