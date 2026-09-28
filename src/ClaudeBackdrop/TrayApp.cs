using System.Diagnostics;
using System.Drawing;
using Microsoft.Win32;

namespace ClaudeBackdrop;

sealed class TrayApp : ApplicationContext
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "ClaudeBackdrop";

    readonly Settings _settings = Settings.Load();
    readonly Backdrop _backdrop;
    readonly NotifyIcon _tray;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    readonly ToolStripMenuItem _status = new("Starting") { Enabled = false };
    readonly ToolStripMenuItem _pause = new("Pause");
    readonly ToolStripMenuItem _animate = new("Animate GIFs");
    readonly ToolStripMenuItem _sidebar = new("Wallpaper behind sidebar");
    readonly ToolStripMenuItem _rendererAuto = new("Automatic (GPU)");
    readonly ToolStripMenuItem _rendererCpu = new("CPU (compatibility)");
    readonly ToolStripMenuItem _startup = new("Start with Windows");
    readonly ToolStripMenuItem _fitCover = new("Cover");
    readonly ToolStripMenuItem _fitFill = new("Stretch");
    readonly List<(ToolStripMenuItem item, double value)> _washItems = new();
    readonly List<(ToolStripMenuItem item, double value)> _blurItems = new();
    readonly List<(ToolStripMenuItem item, double value)> _calmItems = new();
    bool _promptedForBackground;

    public TrayApp()
    {
        _backdrop = new Backdrop(_settings);

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Claude Backdrop") { Enabled = false, Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add(_status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Choose background…", null, (_, _) => ChooseBackground());

        var wash = new ToolStripMenuItem("Readability wash");
        foreach (var (label, v) in new[] { ("None", 0.0), ("Light", 0.35), ("Medium", 0.55), ("Strong", 0.75), ("Very strong", 0.88) })
        {
            var item = new ToolStripMenuItem(label);
            item.Click += (_, _) => Apply(s => s.Wash = v);
            wash.DropDownItems.Add(item);
            _washItems.Add((item, v));
        }
        menu.Items.Add(wash);

        var blur = new ToolStripMenuItem("Blur");
        foreach (var (label, v) in new[] { ("Off", 0.0), ("Soft", 8.0), ("Medium", 19.0), ("Heavy", 40.0) })
        {
            var item = new ToolStripMenuItem(label);
            item.Click += (_, _) => Apply(s => s.BlurPx = v);
            blur.DropDownItems.Add(item);
            _blurItems.Add((item, v));
        }
        menu.Items.Add(blur);

        var calm = new ToolStripMenuItem("Calm behind content");
        foreach (var (label, v) in new[] { ("Off", 0.0), ("Light", 0.5), ("Medium", 0.75), ("Strong", 0.9) })
        {
            var item = new ToolStripMenuItem(label);
            item.Click += (_, _) => Apply(s => s.Calm = v);
            calm.DropDownItems.Add(item);
            _calmItems.Add((item, v));
        }
        menu.Items.Add(calm);

        var fit = new ToolStripMenuItem("Fit");
        _fitCover.Click += (_, _) => Apply(s => s.Fit = "cover");
        _fitFill.Click += (_, _) => Apply(s => s.Fit = "fill");
        fit.DropDownItems.Add(_fitCover);
        fit.DropDownItems.Add(_fitFill);
        menu.Items.Add(fit);

        _animate.Click += (_, _) => Apply(s => s.Animate = !s.Animate);
        menu.Items.Add(_animate);
        _sidebar.Click += (_, _) => Apply(s => s.BlendSidebar = !s.BlendSidebar);
        menu.Items.Add(_sidebar);
        var renderer = new ToolStripMenuItem("Renderer");
        _rendererAuto.Click += (_, _) => Apply(s => s.Renderer = "auto");
        _rendererCpu.Click += (_, _) => Apply(s => s.Renderer = "cpu");
        renderer.DropDownItems.Add(_rendererAuto);
        renderer.DropDownItems.Add(_rendererCpu);
        menu.Items.Add(renderer);
        menu.Items.Add(new ToolStripSeparator());
        _pause.Click += (_, _) => Apply(s => s.Enabled = !s.Enabled);
        menu.Items.Add(_pause);
        _startup.Click += (_, _) => ToggleStartup();
        menu.Items.Add(_startup);
        menu.Items.Add("Open settings folder", null, (_, _) => Process.Start("explorer.exe", Settings.Dir));
        menu.Items.Add("Open log", null, (_, _) => OpenLog());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _tray = new NotifyIcon { Icon = MakeIcon(), Text = "Claude Backdrop", ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => ChooseBackground();

        RefreshChecks();
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
    }

    void OnTick()
    {
        _status.Text = _backdrop.Status;
        _tray.Text = "Claude Backdrop – " + (_backdrop.Status.Length > 45 ? _backdrop.Status[..45] : _backdrop.Status);
        if (!_promptedForBackground && (string.IsNullOrEmpty(_settings.BackgroundPath) || !File.Exists(_settings.BackgroundPath)))
        {
            _promptedForBackground = true;
            ChooseBackground();
        }
    }

    void ChooseBackground()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Choose a background for Claude",
            Filter = "Images and GIFs|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files|*.*",
        };
        if (File.Exists(_settings.BackgroundPath)) dlg.InitialDirectory = Path.GetDirectoryName(_settings.BackgroundPath);
        if (dlg.ShowDialog() == DialogResult.OK) Apply(s => s.BackgroundPath = dlg.FileName);
    }

    void Apply(Action<Settings> change)
    {
        change(_settings);
        _settings.Save();
        _backdrop.UpdateSettings(_settings);
        RefreshChecks();
    }

    void RefreshChecks()
    {
        foreach (var (item, v) in _washItems) item.Checked = Math.Abs(_settings.Wash - v) < 0.01;
        foreach (var (item, v) in _blurItems) item.Checked = Math.Abs(_settings.BlurPx - v) < 0.5;
        foreach (var (item, v) in _calmItems) item.Checked = Math.Abs(_settings.Calm - v) < 0.01;
        _fitCover.Checked = _settings.Fit != "fill";
        _fitFill.Checked = _settings.Fit == "fill";
        _animate.Checked = _settings.Animate;
        _sidebar.Checked = _settings.BlendSidebar;
        _rendererAuto.Checked = _settings.Renderer != "cpu";
        _rendererCpu.Checked = _settings.Renderer == "cpu";
        _pause.Text = _settings.Enabled ? "Pause" : "Resume";
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        _startup.Checked = key?.GetValue(RunValue) != null;
    }

    static void OpenLog()
    {
        string path = Path.Combine(Settings.Dir, "log.txt");
        if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    void ToggleStartup()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (key.GetValue(RunValue) != null) key.DeleteValue(RunValue);
        else key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
        RefreshChecks();
    }

    static Icon MakeIcon()
    {
        using var stream = typeof(TrayApp).Assembly.GetManifestResourceStream("ClaudeBackdrop.ico");
        return new Icon(stream, SystemInformation.SmallIconSize);
    }
    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        _backdrop.Dispose();
        base.ExitThreadCore();
    }
}
