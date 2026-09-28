using System.Text.Json;

namespace ClaudeBackdrop;

sealed class Settings
{
    public string BackgroundPath { get; set; } = "";
    public string Fit { get; set; } = "cover";      // cover | fill
    public double Wash { get; set; } = 0.55;         // 0..1 blend of wallpaper toward Claude's own background color
    public double BlurPx { get; set; } = 0;
    public bool Animate { get; set; } = true;
    public bool Enabled { get; set; } = true;
    public int KeyTolerance { get; set; } = 1;       // max per-channel distance treated as Claude background
    public bool BlendSidebar { get; set; } = false;  // also show the wallpaper behind the sidebar
    public string Renderer { get; set; } = "auto";   // auto (GPU, CPU fallback) | cpu
    public double Calm { get; set; } = 0.75;         // how much the wallpaper calms toward Claude's background behind content
    public bool Trace { get; set; } = false;         // log visibility transitions (diagnostics)

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeBackdrop");
    public static string FilePath => Path.Combine(Dir, "config.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { }
        var s = new Settings();
        s.ImportFromCodexBackdrop();
        s.Save();
        return s;
    }

    // First run: reuse the wallpaper and look already chosen in Codex Backdrop, if any.
    void ImportFromCodexBackdrop()
    {
        try
        {
            string codex = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexBackdrop", "config.json");
            if (!File.Exists(codex)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(codex));
            var root = doc.RootElement;
            string[] images = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"];
            if (root.TryGetProperty("backgroundPath", out var bg) && File.Exists(bg.GetString()) &&
                images.Contains(Path.GetExtension(bg.GetString()), StringComparer.OrdinalIgnoreCase)) BackgroundPath = bg.GetString();
            if (root.TryGetProperty("blurPx", out var blur) && blur.TryGetDouble(out double b)) BlurPx = Math.Round(b);
            if (root.TryGetProperty("canvasOpacity", out var wash) && wash.TryGetDouble(out double w)) Wash = w;
            if (root.TryGetProperty("surfaceOpacity", out var calm) && calm.TryGetDouble(out double c)) Calm = c;
            if (root.TryGetProperty("mediaFit", out var fit) && fit.GetString() == "fill") Fit = "fill";
            if (root.TryGetProperty("animate", out var animate) && animate.ValueKind is JsonValueKind.True or JsonValueKind.False) Animate = animate.GetBoolean();
            if (root.TryGetProperty("sidebarStyle", out var side)) BlendSidebar = side.GetString() != "solid";
        }
        catch { }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { }
    }

    public Settings Clone() => (Settings)MemberwiseClone();
}
