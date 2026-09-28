using System.Text;
using System.Text.Json;
using CodexBackdrop;

var scratch = Path.Combine(
    Path.GetTempPath(),
    $"codex-backdrop-settings-chaos-{Guid.NewGuid():N}");
Directory.CreateDirectory(scratch);

try
{
    TestDefaultsAndInvalidDocuments(scratch);
    TestLegacyAndMalformedValues(scratch);
    TestAtomicSaveAndBackup(scratch);
    TestTransientBackupLock(scratch);
    TestBackupAndPendingWriteRecovery(scratch);
    TestMediaAvailabilityIsNonCrashing(scratch);
    TestOversizedSavePreservesConfig(scratch);
    Console.WriteLine("settings durability chaos: all checks passed");
}
finally
{
    try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
    catch { }
}

static void TestOversizedSavePreservesConfig(string root)
{
    var path = Path.Combine(CaseDirectory(root, "oversized-save"), "config.json");
    new BackdropSettings { CanvasOpacity = 0.27 }.Save(path);
    var original = File.ReadAllBytes(path);
    var oversized = new BackdropSettings { BackgroundPath = new string('x', 1024 * 1024) };
    var rejected = false;
    try { oversized.Save(path); }
    catch (InvalidDataException) { rejected = true; }
    Expect(rejected, "oversized config must be rejected before replacing readable settings");
    Expect(File.ReadAllBytes(path).SequenceEqual(original), "failed save must preserve the primary config");
    Expect(BackdropSettings.Load(path).CanvasOpacity == 0.27, "last good settings remain readable");
}

static void TestDefaultsAndInvalidDocuments(string root)
{
    var directory = CaseDirectory(root, "defaults");
    var path = Path.Combine(directory, "config.json");

    var missing = BackdropSettings.Load(path);
    Expect(missing.SchemaVersion == 8, "missing config uses current schema");
    Expect(missing.BackgroundPath == "", "missing config has no background");
    Expect(missing.DevToolsPort == 9223, "missing config uses default port");
    Expect(missing.HasUsableBackground == false, "missing config disables media");

    File.WriteAllText(path, "not json", Encoding.UTF8);
    var malformed = BackdropSettings.Load(path);
    Expect(malformed.SchemaVersion == 8, "malformed config falls back to current defaults");
    Expect(malformed.CanvasOpacity == 0.55, "malformed config does not leak partial values");

    File.WriteAllText(path, "[]", Encoding.UTF8);
    var wrongRoot = BackdropSettings.Load(path);
    Expect(wrongRoot.SchemaVersion == 8, "non-object config falls back safely");
}

static void TestLegacyAndMalformedValues(string root)
{
    var directory = CaseDirectory(root, "normalization");
    var path = Path.Combine(directory, "config.json");
    File.WriteAllText(path, """
        {
          "schemaVersion": 7,
          "backgroundPath": null,
          "backgroundOpacity": "NaN",
          "canvasOpacity": "Infinity",
          "surfaceOpacity": -1,
          "sidebarOpacity": 99,
          "sidebarStyle": " SOLID ",
          "topOpacity": "-Infinity",
          "topStyle": "unknown",
          "overlayOpacity": 99,
          "blurPx": "NaN",
          "brightness": 99,
          "saturation": -1,
          "playbackRate": 99,
          "mediaFit": " CONTAIN ",
          "mediaPosition": " BOTTOM CENTER ",
          "devToolsPort": 80,
          "rendererProfilePath": null,
          "keepAttached": true
        }
        """, Encoding.UTF8);

    var loaded = BackdropSettings.Load(path);
    Expect(loaded.BackgroundPath == "", "null path normalizes to empty");
    Expect(loaded.BackgroundOpacity == 1.0, "nan opacity uses the default");
    Expect(loaded.CanvasOpacity == 0.55, "infinite opacity uses the default");
    Expect(loaded.SurfaceOpacity == 0 && loaded.SidebarOpacity == 0.95, "opacity bounds are enforced");
    Expect(loaded.SidebarStyle == "solid" && loaded.TopStyle == "blended", "region styles are canonicalized");
    Expect(loaded.TopOpacity == 0.32 && loaded.OverlayOpacity == 0.85, "non-finite and high opacities are safe");
    Expect(loaded.BlurPx == 0 && loaded.Brightness == 1.5, "visual bounds are finite");
    Expect(loaded.Saturation == 0 && loaded.PlaybackRate == 2, "media bounds are enforced");
    Expect(loaded.MediaFit == "contain" && loaded.MediaPosition == "bottom center", "media choices are canonicalized");
    Expect(loaded.DevToolsPort == 9223 && loaded.RendererProfilePath == "", "unsafe connection values use defaults");

    File.WriteAllText(path, "{\"canvasOpacity\":0.7,\"topOpacity\":0.8}", Encoding.UTF8);
    var legacy = BackdropSettings.Load(path);
    Expect(legacy.SchemaVersion == 8, "missing schema migrates to current version");
    Expect(legacy.CanvasOpacity == 0.32 && legacy.TopOpacity == 0.32, "legacy schema receives historical defaults");
}

static void TestAtomicSaveAndBackup(string root)
{
    var directory = CaseDirectory(root, "atomic-save");
    var path = Path.Combine(directory, "config.json");
    var staleTemporary = path + ".tmp";
    File.WriteAllText(staleTemporary, "sentinel", Encoding.UTF8);

    var first = new BackdropSettings { CanvasOpacity = 0.21 };
    first.Save(path);
    Expect(File.Exists(path), "first save creates the config");
    Expect(File.ReadAllText(staleTemporary, Encoding.UTF8) == "sentinel", "legacy temp is never overwritten");
    Expect(Directory.GetFiles(directory, "config.json.*.tmp").Length == 0, "temporary file is cleaned after first save");

    var second = new BackdropSettings { CanvasOpacity = 0.79 };
    second.Save(path);
    var current = BackdropSettings.Load(path);
    Expect(Math.Abs(current.CanvasOpacity - 0.79) < 0.0001, "replacement publishes the newest config");
    Expect(File.Exists(path + ".bak"), "replacement keeps a backup");
    var backup = BackdropSettings.Load(path + ".bak");
    Expect(Math.Abs(backup.CanvasOpacity - 0.21) < 0.0001, "backup contains the previous config");
    Expect(Directory.GetFiles(directory, "config.json.*.tmp").Length == 0, "temporary file is cleaned after replacement");
}

static void TestTransientBackupLock(string root)
{
    var directory = CaseDirectory(root, "transient-backup-lock");
    var path = Path.Combine(directory, "config.json");
    new BackdropSettings { BackgroundOpacity = 0.31 }.Save(path);
    new BackdropSettings { BackgroundOpacity = 0.32 }.Save(path);

    var backup = path + ".bak";
    var blocker = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.Read);
    var release = Task.Run(() =>
    {
        Thread.Sleep(150);
        blocker.Dispose();
    });

    new BackdropSettings { BackgroundOpacity = 0.33 }.Save(path);
    release.GetAwaiter().GetResult();

    var current = BackdropSettings.Load(path);
    Expect(Math.Abs(current.BackgroundOpacity - 0.33) < 0.0001, "a transient backup lock does not lose the newest setting");
}

static void TestBackupAndPendingWriteRecovery(string root)
{
    var backupDirectory = CaseDirectory(root, "backup-recovery");
    var backupPath = Path.Combine(backupDirectory, "config.json");
    new BackdropSettings { CanvasOpacity = 0.44 }.Save(backupPath);
    new BackdropSettings { CanvasOpacity = 0.66 }.Save(backupPath);
    File.WriteAllText(backupPath, "{", Encoding.UTF8);

    var recovered = BackdropSettings.Load(backupPath);
    Expect(Math.Abs(recovered.CanvasOpacity - 0.44) < 0.0001, "corrupt primary recovers from backup");
    Expect(IsJsonObject(backupPath), "backup recovery repairs the primary atomically");

    var pendingDirectory = CaseDirectory(root, "pending-recovery");
    var pendingPath = Path.Combine(pendingDirectory, "config.json");
    File.WriteAllText(pendingPath, "{", Encoding.UTF8);
    var pendingTemporary = Path.Combine(pendingDirectory, $"config.json.{Guid.NewGuid():N}.tmp");
    var pending = new BackdropSettings { CanvasOpacity = 0.58 };
    File.WriteAllText(pendingTemporary, JsonSerializer.Serialize(pending, JsonOptions()), Encoding.UTF8);

    var pendingLoaded = BackdropSettings.Load(pendingPath);
    Expect(Math.Abs(pendingLoaded.CanvasOpacity - 0.58) < 0.0001, "flushed pending write recovers when primary is corrupt");
    Expect(IsJsonObject(pendingPath), "pending write recovery repairs the primary");
}

static void TestMediaAvailabilityIsNonCrashing(string root)
{
    var directory = CaseDirectory(root, "media");
    var path = Path.Combine(directory, "config.json");
    var missing = Path.Combine(directory, "later", "wallpaper.PNG");
    var settings = new BackdropSettings { BackgroundPath = missing };
    settings.Save(path);

    var loaded = BackdropSettings.Load(path);
    Expect(loaded.BackgroundPath == missing, "missing media path is preserved");
    Expect(!loaded.HasUsableBackground && loaded.EffectiveBackgroundPath == "", "missing media is disabled without throwing");

    Directory.CreateDirectory(Path.GetDirectoryName(missing)!);
    File.WriteAllBytes(missing, [1, 2, 3]);
    Expect(loaded.HasUsableBackground && loaded.EffectiveBackgroundPath == missing, "a restored media file re-enables automatically");

    var unsupported = Path.Combine(directory, "wallpaper.txt");
    File.WriteAllText(unsupported, "not media", Encoding.UTF8);
    loaded.BackgroundPath = unsupported;
    loaded.Save(path);
    var unsupportedLoaded = BackdropSettings.Load(path);
    Expect(unsupportedLoaded.BackgroundPath == unsupported, "unsupported path is preserved");
    Expect(!unsupportedLoaded.HasUsableBackground && unsupportedLoaded.EffectiveBackgroundPath == "", "unsupported media is disabled safely");
}

static string CaseDirectory(string root, string name)
{
    var directory = Path.Combine(root, name);
    Directory.CreateDirectory(directory);
    return directory;
}

static bool IsJsonObject(string path)
{
    using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
    return document.RootElement.ValueKind == JsonValueKind.Object;
}

static JsonSerializerOptions JsonOptions() => new()
{
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true,
};

static void Expect(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("failed: " + message);
}
