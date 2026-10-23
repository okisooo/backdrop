using System.Text.Json;

namespace Backdrop;

sealed class Settings
{
    public string BackgroundPath { get; set; } = "";
    public string Fit { get; set; } = "cover";      // cover | fill
    public double Wash { get; set; } = 0.55;         // 0..1 blend of wallpaper toward app's own background color
    public double BlurPx { get; set; } = 0;
    public bool Animate { get; set; } = true;
    public bool Enabled { get; set; } = true;
    public bool EnableClaude { get; set; } = true;
    public bool EnableCodex { get; set; } = true;
    public bool EnableT3Code { get; set; } = true;
    public int KeyTolerance { get; set; } = 1;       // max per-channel distance treated as background (T3 Code uses >= 4 for grain)
    public bool BlendSidebar { get; set; } = false;  // also show the wallpaper behind the sidebar and window frame
    public string Renderer { get; set; } = "auto";   // auto (GPU, CPU fallback) | cpu
    public double Calm { get; set; } = 0.75;         // how much the wallpaper calms toward the app's background behind content
    public bool Trace { get; set; } = false;         // log visibility transitions (diagnostics)

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static readonly string[] Images = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"];

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Backdrop");
    public static string FilePath => Path.Combine(Dir, "config.json");

    public bool IsAppEnabled(TargetApp app) => Enabled && app switch
    {
        TargetApp.Claude => EnableClaude,
        TargetApp.Codex => EnableCodex,
        TargetApp.T3Code => EnableT3Code,
        _ => true
    };

    public int EffectiveTolerance(TargetApp app) =>
        app == TargetApp.T3Code ? Math.Max(KeyTolerance, 4) : KeyTolerance;

    public static Settings Load() => LoadFrom(Dir, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    internal static Settings LoadFrom(string dir, string localAppData)
    {
        string filePath = Path.Combine(dir, "config.json");
        try
        {
            if (File.Exists(filePath))
            {
                string text = File.ReadAllText(filePath);
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("schemaVersion", out _))
                {
                    var migrated = FromVersion1(doc.RootElement);
                    File.Copy(filePath, Path.Combine(dir, "config.v1.json"), overwrite: true);
                    migrated.SaveTo(dir);
                    return migrated;
                }
                var loaded = JsonSerializer.Deserialize<Settings>(text);
                if (loaded != null) return loaded;
            }
        }
        catch
        {
            try
            {
                if (File.Exists(filePath))
                    File.Copy(filePath, Path.Combine(dir, "config.corrupt.json"), overwrite: true);
            }
            catch { }
        }
        var s = new Settings();
        s.ImportFromLegacy(localAppData);
        s.SaveTo(dir);
        return s;
    }

    internal static Settings FromVersion1(JsonElement root)
    {
        var s = new Settings();
        if (root.TryGetProperty("backgroundPath", out var bg) && bg.GetString() is { Length: > 0 } path &&
            Images.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) s.BackgroundPath = path;
        if (root.TryGetProperty("blurPx", out var blur) && blur.TryGetDouble(out double b)) s.BlurPx = Nearest(b, 0, 8, 19, 40);
        if (root.TryGetProperty("canvasOpacity", out var wash) && wash.TryGetDouble(out double w)) s.Wash = Nearest(w, 0, 0.35, 0.55, 0.75, 0.88);
        if (root.TryGetProperty("surfaceOpacity", out var calm) && calm.TryGetDouble(out double c)) s.Calm = Nearest(c, 0, 0.5, 0.75, 0.9);
        if (root.TryGetProperty("mediaFit", out var fit)) s.Fit = fit.GetString() == "fill" ? "fill" : "cover";
        if (root.TryGetProperty("animate", out var animate) && animate.ValueKind is JsonValueKind.True or JsonValueKind.False) s.Animate = animate.GetBoolean();
        if (root.TryGetProperty("sidebarStyle", out var side)) s.BlendSidebar = side.GetString() != "solid";
        if (root.TryGetProperty("keepAttached", out var keep) && keep.ValueKind is JsonValueKind.True or JsonValueKind.False) s.Enabled = keep.GetBoolean();
        return s;
    }

    static double Nearest(double value, params double[] presets) => presets.MinBy(p => Math.Abs(p - value));

    internal void ImportFromLegacy(string localAppData)
    {
        string[] candidates =
        [
            Path.Combine(localAppData, "CodexBackdrop", "config.json"),
            Path.Combine(localAppData, "ClaudeBackdrop", "config.json"),
            Path.Combine(localAppData, "T3Backdrop", "config.json")
        ];
        bool IsImage(string path) => !string.IsNullOrEmpty(path) && File.Exists(path) && Images.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

        foreach (string path in candidates)
        {
            try
            {
                if (!File.Exists(path)) continue;
                string text = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                Settings other = null;
                if (root.TryGetProperty("schemaVersion", out _))
                {
                    other = FromVersion1(root);
                    if (!IsImage(other.BackgroundPath)) other.BackgroundPath = "";
                }
                else if (root.TryGetProperty(nameof(BackgroundPath), out _))
                {
                    other = JsonSerializer.Deserialize<Settings>(text);
                    if (other != null && !IsImage(other.BackgroundPath)) other.BackgroundPath = "";
                }
                if (other == null) continue;

                BackgroundPath = other.BackgroundPath;
                Fit = other.Fit == "fill" ? "fill" : "cover";
                Wash = other.Wash;
                BlurPx = other.BlurPx;
                Animate = other.Animate;
                Enabled = other.Enabled;
                KeyTolerance = Math.Clamp(other.KeyTolerance, 0, 32);
                BlendSidebar = other.BlendSidebar;
                Renderer = other.Renderer == "cpu" ? "cpu" : "auto";
                Calm = other.Calm;
                Trace = other.Trace;
                if (!string.IsNullOrEmpty(BackgroundPath)) return;
            }
            catch { }
        }
    }

    public void Save() => SaveTo(Dir);

    internal void SaveTo(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string filePath = Path.Combine(dir, "config.json");
            string tmp = filePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, filePath, overwrite: true);
        }
        catch { }
    }

    public Settings Clone() => (Settings)MemberwiseClone();
}