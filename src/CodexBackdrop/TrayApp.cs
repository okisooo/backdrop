using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace CodexBackdrop;

sealed class TrayApp : ApplicationContext
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "CodexBackdrop";

    readonly AppPaths _paths = AppPaths.Create();
    readonly BackdropSettings _settings;
    readonly RuntimeManager _runtime;
    readonly CodexSupervisor _supervisor;
    readonly NotifyIcon _tray;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    readonly ToolStripMenuItem _status = new("Starting") { Enabled = false };
    readonly ToolStripMenuItem _pause = new("Pause");
    readonly ToolStripMenuItem _animate = new("Animate GIFs");
    readonly ToolStripMenuItem _sidebar = new("Wallpaper behind sidebar");
    readonly ToolStripMenuItem _startup = new("Start with Windows");
    readonly ToolStripMenuItem _fitCover = new("Cover");
    readonly ToolStripMenuItem _fitFill = new("Stretch");
    readonly List<(ToolStripMenuItem item, double value)> _washItems = new();
    readonly List<(ToolStripMenuItem item, double value)> _blurItems = new();
    readonly List<(ToolStripMenuItem item, double value)> _calmItems = new();
    volatile string _statusText = "Starting";
    bool _promptedForBackground;

    public TrayApp()
    {
        bool firstRun = !File.Exists(_paths.ConfigPath);
        _settings = BackdropSettings.Load(_paths.ConfigPath);
        if (firstRun) ImportFromClaudeBackdrop();
        _runtime = new RuntimeManager(_paths);
        _supervisor = new CodexSupervisor(_runtime, _paths, _settings.KeepAttached);
        _supervisor.StatusChanged += status => _statusText = Describe(status.Text);

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Codex Backdrop") { Enabled = false, Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add(_status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Choose background…", null, (_, _) => ChooseBackground());

        var wash = new ToolStripMenuItem("Readability wash");
        foreach (var (label, v) in new[] { ("None", 0.0), ("Light", 0.35), ("Medium", 0.55), ("Strong", 0.75), ("Very strong", 0.88) })
        {
            var item = new ToolStripMenuItem(label);
            item.Click += (_, _) => Apply(s => s.CanvasOpacity = v);
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
            item.Click += (_, _) => Apply(s => s.SurfaceOpacity = v);
            calm.DropDownItems.Add(item);
            _calmItems.Add((item, v));
        }
        menu.Items.Add(calm);

        var fit = new ToolStripMenuItem("Fit");
        _fitCover.Click += (_, _) => Apply(s => s.MediaFit = "cover");
        _fitFill.Click += (_, _) => Apply(s => s.MediaFit = "fill");
        fit.DropDownItems.Add(_fitCover);
        fit.DropDownItems.Add(_fitFill);
        menu.Items.Add(fit);

        _animate.Click += (_, _) => Apply(s => s.Animate = !s.Animate);
        menu.Items.Add(_animate);
        _sidebar.Click += (_, _) => Apply(s => s.SidebarStyle = s.TopStyle = s.SidebarStyle == "solid" ? "blended" : "solid");
        menu.Items.Add(_sidebar);
        // Codex needs one restart with its local debugging port before the
        // backdrop can attach; this does it now instead of on the next launch.
        menu.Items.Add("Attach now", null, (_, _) => AttachNow());
        menu.Items.Add(new ToolStripSeparator());
        _pause.Click += (_, _) => TogglePause();
        menu.Items.Add(_pause);
        _startup.Click += (_, _) => ToggleStartup();
        menu.Items.Add(_startup);
        menu.Items.Add("Open settings folder", null, (_, _) => Process.Start("explorer.exe", _paths.DataDirectory));
        menu.Items.Add("Open log", null, (_, _) => OpenLog());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _tray = new NotifyIcon { Icon = MakeIcon(), Text = "Codex Backdrop", ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => ChooseBackground();

        RefreshChecks();
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
        _supervisor.Start();
    }

    void OnTick()
    {
        var status = _statusText;
        _status.Text = status.Length > 90 ? status[..89] + "…" : status;
        _tray.Text = "Codex Backdrop – " + (status.Length > 45 ? status[..45] : status);
        if (!_promptedForBackground && !_settings.HasUsableBackground)
        {
            _promptedForBackground = true;
            ChooseBackground();
        }
    }

    // First run: reuse the wallpaper and look already chosen in Claude Backdrop, if any.
    void ImportFromClaudeBackdrop()
    {
        try
        {
            string claude = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeBackdrop", "config.json");
            if (!File.Exists(claude)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(claude));
            var root = doc.RootElement;
            if (root.TryGetProperty("BackgroundPath", out var bg) && File.Exists(bg.GetString())) _settings.BackgroundPath = bg.GetString()!;
            if (root.TryGetProperty("BlurPx", out var blur) && blur.TryGetDouble(out double b)) _settings.BlurPx = b;
            if (root.TryGetProperty("Wash", out var wash) && wash.TryGetDouble(out double w)) _settings.CanvasOpacity = w;
            if (root.TryGetProperty("Calm", out var calm) && calm.TryGetDouble(out double c)) _settings.SurfaceOpacity = c;
            if (root.TryGetProperty("Fit", out var fit)) _settings.MediaFit = fit.GetString() == "fill" ? "fill" : "cover";
            if (root.TryGetProperty("Animate", out var animate) && animate.ValueKind is JsonValueKind.True or JsonValueKind.False)
                _settings.Animate = animate.GetBoolean();
            if (root.TryGetProperty("BlendSidebar", out var side) && side.ValueKind is JsonValueKind.True or JsonValueKind.False)
                _settings.SidebarStyle = _settings.TopStyle = side.GetBoolean() ? "blended" : "solid";
            _settings.Save(_paths.ConfigPath);
        }
        catch { }
    }

    void ChooseBackground()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Choose a background for Codex",
            Filter = "Images, GIFs and videos|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.mp4;*.webm|All files|*.*",
        };
        if (File.Exists(_settings.BackgroundPath)) dlg.InitialDirectory = Path.GetDirectoryName(_settings.BackgroundPath);
        if (dlg.ShowDialog() == DialogResult.OK) Apply(s => s.BackgroundPath = dlg.FileName);
    }

    void Apply(Action<BackdropSettings> change)
    {
        change(_settings);
        try { _settings.Save(_paths.ConfigPath); }
        catch (Exception error) { _statusText = "Settings not saved: " + error.Message; }
        _runtime.NudgeInjector();
        RefreshChecks();
    }

    async void AttachNow()
    {
        Apply(s => s.KeepAttached = true);
        try { await _supervisor.RetryNowAsync(launchIfClosed: true); }
        catch (Exception error) { _statusText = Describe(error.Message); }
    }

    async void TogglePause()
    {
        bool pause = _settings.KeepAttached;
        Apply(s => s.KeepAttached = !pause);
        _supervisor.SetEnabled(!pause);
        if (!pause) return;
        try { await _runtime.RemoveBackdropAsync(); }
        catch (Exception error) { _statusText = Describe(error.Message); }
    }

    void RefreshChecks()
    {
        foreach (var (item, v) in _washItems) item.Checked = Math.Abs(_settings.CanvasOpacity - v) < 0.01;
        foreach (var (item, v) in _blurItems) item.Checked = Math.Abs(_settings.BlurPx - v) < 0.5;
        foreach (var (item, v) in _calmItems) item.Checked = Math.Abs(_settings.SurfaceOpacity - v) < 0.01;
        _fitCover.Checked = _settings.MediaFit != "fill";
        _fitFill.Checked = _settings.MediaFit == "fill";
        _animate.Checked = _settings.Animate;
        _sidebar.Checked = _settings.SidebarStyle != "solid";
        _pause.Text = _settings.KeepAttached ? "Pause" : "Resume";
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        _startup.Checked = key?.GetValue(RunValue) != null;
    }

    // Supervisor states read as lowercase log lines; show them like the tray's other labels.
    static string Describe(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "Starting";
        text = text.Replace("codex", "Codex", StringComparison.Ordinal);
        return text switch
        {
            "watching for Codex" => "Waiting for Codex",
            "backdrop attached" => "Attached",
            "automatic attachment paused" => "Paused",
            // Failure codes (bd001…) are quoted from the log as they are.
            _ when text.StartsWith("bd", StringComparison.Ordinal) && text.Length > 4 && char.IsDigit(text[2]) => text,
            _ => char.ToUpperInvariant(text[0]) + text[1..],
        };
    }

    void OpenLog()
    {
        if (File.Exists(_paths.LogPath)) Process.Start(new ProcessStartInfo(_paths.LogPath) { UseShellExecute = true });
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
        using var stream = typeof(TrayApp).Assembly.GetManifestResourceStream("CodexBackdrop.ico");
        return new Icon(stream!, SystemInformation.SmallIconSize);
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        // Leave Codex running and looking exactly as it does without the backdrop.
        try { _supervisor.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10)); } catch { }
        try { _runtime.RemoveBackdropAsync().Wait(TimeSpan.FromSeconds(6)); } catch { }
        try { _runtime.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)); } catch { }
        base.ExitThreadCore();
    }
}
