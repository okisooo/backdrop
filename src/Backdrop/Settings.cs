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
    public bool BlendSidebar { get; set; } = false;  // also show the wallpaper behind the sidebar
    public string Renderer { get; set; } = "auto";   // auto (GPU, CPU fallback) | cpu
    public double Calm { get; set; } = 0.75;         // how much the wallpaper calms toward the app's background behind content
    public bool Trace { get; set; } = false;         // log visibility transitions (diagnostics)

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

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

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { }
        var s = new Settings();
        s.ImportFromLegacy();
        s.Save();
        return s;
    }

    void ImportFromLegacy()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        [
            Path.Combine(local, "T3Backdrop", "config.json"),
            Path.Combine(local, "ClaudeBackdrop", "config.json"),
            Path.Combine(local, "CodexBackdrop", "config.json")
        ];
        string[] images = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"];
        bool IsImage(string path) => !string.IsNullOrEmpty(path) && File.Exists(path) && images.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

        foreach (string path in candidates)
        {
            try
            {
                if (!File.Exists(path)) continue;
                string text = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.TryGetProperty(nameof(BackgroundPath), out _))
                {
                    var other = JsonSerializer.Deserialize<Settings>(text);
                    if (other == null) continue;
                    if (IsImage(other.BackgroundPath)) BackgroundPath = other.BackgroundPath;
                    (Fit, Wash, BlurPx, Animate, Calm, BlendSidebar) =
                        (other.Fit, other.Wash, other.BlurPx, other.Animate, other.Calm, other.BlendSidebar);
                    if (!string.IsNullOrEmpty(BackgroundPath)) return;
                }
            }
            catch { }
        }
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