using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexBackdrop;

public sealed class BackdropSettings
{
    private const int CurrentSchemaVersion = 8;
    private const long MaximumConfigBytes = 1 * 1024 * 1024;
    private const int AtomicReplaceAttempts = 6;
    private const int AtomicReplaceRetryDelayMilliseconds = 40;
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string BackgroundPath { get; set; } = "";
    public double BackgroundOpacity { get; set; } = 1.0;
    public double CanvasOpacity { get; set; } = 0.55;
    public double SurfaceOpacity { get; set; } = 0.75;
    public double SidebarOpacity { get; set; } = 0.50;
    public string SidebarStyle { get; set; } = "solid";
    public double TopOpacity { get; set; } = 0.32;
    public string TopStyle { get; set; } = "solid";
    public double OverlayOpacity { get; set; } = 0;
    public double BlurPx { get; set; } = 0;
    public double Brightness { get; set; } = 1.0;
    public double Saturation { get; set; } = 1.0;
    public double PlaybackRate { get; set; } = 1.0;
    public string MediaFit { get; set; } = "cover";
    public string MediaPosition { get; set; } = "center center";
    public int DevToolsPort { get; set; } = 9223;
    public string RendererProfilePath { get; set; } = "";
    public bool KeepAttached { get; set; } = true;
    public bool Animate { get; set; } = true;

    [JsonIgnore]
    public static string[] SupportedExtensions => [".png", ".jpg", ".jpeg", ".webp", ".gif", ".mp4", ".webm"];

    // Keep the selected path in the config even when the file is temporarily
    // absent or has an unsupported extension. Consumers can use the effective
    // path to disable media without losing the user's choice.
    [JsonIgnore]
    public bool HasUsableBackground => TryGetUsableBackgroundPath(out _);

    [JsonIgnore]
    public string EffectiveBackgroundPath => TryGetUsableBackgroundPath(out var path) ? path : "";

    public bool TryGetUsableBackgroundPath(out string path)
    {
        path = "";
        var candidate = BackgroundPath?.Trim() ?? "";
        if (candidate.Length == 0) return false;

        try
        {
            var extension = Path.GetExtension(candidate);
            if (!SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) return false;
            if (!File.Exists(candidate)) return false;
            path = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static BackdropSettings Load(string path)
    {
        var defaults = new BackdropSettings().Normalize();
        if (!TryResolvePath(path, out var destination)) return defaults;

        if (TryRead(destination, out var loaded)) return loaded;

        foreach (var recovery in RecoveryCandidates(destination))
        {
            if (!TryRead(recovery, out loaded)) continue;

            // Best-effort repair means a later save does not accidentally
            // replace the only good backup with the corrupt primary.
            TryRestore(destination, recovery);
            return loaded;
        }

        return defaults;
    }

    public void Save(string path)
    {
        if (!TryResolvePath(path, out var destination))
            throw new ArgumentException("a config path is required", nameof(path));

        var directory = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("config path has no parent directory");

        Directory.CreateDirectory(directory);
        Normalize();
        var json = JsonSerializer.Serialize(this, JsonOptions());
        var bytes = Utf8NoBom.GetBytes(json);
        if (bytes.Length > MaximumConfigBytes)
            throw new InvalidDataException("settings exceed the 1 MiB configuration limit");
        var temporary = CreateTemporaryPath(destination);

        try
        {
            using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                options: FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            AtomicReplace(temporary, destination, destination + ".bak");
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public BackdropSettings Normalize()
    {
        Migrate();
        BackgroundPath = NormalizePath(BackgroundPath);
        BackgroundOpacity = ClampFinite(BackgroundOpacity, 0, 1, 1.0);
        CanvasOpacity = ClampFinite(CanvasOpacity, 0, 0.95, 0.55);
        SurfaceOpacity = ClampFinite(SurfaceOpacity, 0, 0.95, 0.75);
        SidebarOpacity = ClampFinite(SidebarOpacity, 0.05, 0.95, 0.50);
        SidebarStyle = NormalizeRegionStyle(SidebarStyle);
        TopOpacity = ClampFinite(TopOpacity, 0.05, 0.95, 0.32);
        TopStyle = NormalizeRegionStyle(TopStyle);
        OverlayOpacity = ClampFinite(OverlayOpacity, 0, 0.85, 0);
        BlurPx = ClampFinite(BlurPx, 0, 40, 0);
        Brightness = ClampFinite(Brightness, 0.2, 1.5, 1.0);
        Saturation = ClampFinite(Saturation, 0, 2, 1.0);
        PlaybackRate = ClampFinite(PlaybackRate, 0.25, 2, 1.0);
        MediaFit = NormalizeMediaFit(MediaFit);
        MediaPosition = NormalizeMediaPosition(MediaPosition);
        if (DevToolsPort is < 1024 or > 65535) DevToolsPort = 9223;
        RendererProfilePath = NormalizePath(RendererProfilePath);
        return this;
    }

    private BackdropSettings Migrate()
    {
        if (SchemaVersion < 4)
        {
            CanvasOpacity = 0.32;
            SurfaceOpacity = 0.54;
            SidebarOpacity = 0.50;
            BackgroundOpacity = 0.85;
            OverlayOpacity = 0.03;
            Brightness = 1.0;
            Saturation = 0.9;
        }
        if (SchemaVersion < 5)
        {
            SidebarStyle = "blended";
            TopStyle = "blended";
            TopOpacity = CanvasOpacity;
        }
        if (SchemaVersion < 6) RendererProfilePath = "";
        if (SchemaVersion < 7) KeepAttached = true;
        if (SchemaVersion < 8) Animate = true;
        SchemaVersion = CurrentSchemaVersion;
        return this;
    }

    private static string NormalizeRegionStyle(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "transparent" => "transparent",
        "solid" => "solid",
        _ => "blended",
    };

    private static string NormalizeMediaFit(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "contain" => "contain",
        "fill" => "fill",
        _ => "cover",
    };

    private static string NormalizeMediaPosition(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "top center" => "top center",
        "bottom center" => "bottom center",
        _ => "center center",
    };

    private static string NormalizePath(string? value) => value?.Trim() ?? "";

    private static double ClampFinite(double value, double minimum, double maximum, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

    private static bool TryResolvePath(string? path, out string resolved)
    {
        resolved = "";
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            resolved = Path.GetFullPath(path);
            return resolved.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryRead(string path, out BackdropSettings settings)
    {
        settings = null!;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaximumConfigBytes) return false;

            var json = File.ReadAllText(path, Utf8NoBom);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;

            var loaded = document.Deserialize<BackdropSettings>(JsonOptions());
            if (loaded is null) return false;
            if (!HasSchemaVersion(document.RootElement)) loaded.SchemaVersion = 1;
            settings = loaded.Normalize();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasSchemaVersion(JsonElement root) => root.EnumerateObject()
        .Any(property => string.Equals(property.Name, "schemaVersion", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> RecoveryCandidates(string destination)
    {
        var backup = destination + ".bak";
        if (File.Exists(backup)) yield return backup;

        var directory = Path.GetDirectoryName(destination);
        var fileName = Path.GetFileName(destination);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName)) yield break;

        string[] files;
        try { files = Directory.GetFiles(directory); }
        catch { yield break; }

        var prefix = fileName + ".";
        foreach (var candidate in files
            .Where(file =>
            {
                var name = Path.GetFileName(file);
                return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
            })
            .OrderByDescending(GetLastWriteTimeUtcSafe))
        {
            yield return candidate;
        }
    }

    private static DateTime GetLastWriteTimeUtcSafe(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }

    private static string CreateTemporaryPath(string destination) =>
        Path.Combine(
            Path.GetDirectoryName(destination)!,
            $"{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");

    private static void AtomicReplace(string temporary, string destination, string backup)
    {
        if (!File.Exists(destination))
        {
            File.Move(temporary, destination);
            return;
        }

        for (var attempt = 1; attempt <= AtomicReplaceAttempts; attempt++)
        {
            try
            {
                File.Replace(temporary, destination, backup, ignoreMetadataErrors: true);
                return;
            }
            catch (IOException) when (attempt < AtomicReplaceAttempts)
            {
                Thread.Sleep(AtomicReplaceRetryDelayMilliseconds);
            }
            catch (UnauthorizedAccessException) when (attempt < AtomicReplaceAttempts)
            {
                Thread.Sleep(AtomicReplaceRetryDelayMilliseconds);
            }
            catch (PlatformNotSupportedException) { break; }
            catch (NotSupportedException) { break; }
        }

        // File.Replace is atomic on the supported Windows filesystems. Keep a
        // durable backup for unusual filesystems where that primitive is not
        // available, then perform the closest safe same-directory replacement.
        File.Copy(destination, backup, overwrite: true);
        File.Move(temporary, destination, overwrite: true);
    }

    private static void TryRestore(string destination, string recovery)
    {
        var temporary = CreateTemporaryPath(destination);
        string? displaced = null;
        try
        {
            CopyDurably(recovery, temporary);
            if (File.Exists(destination)) displaced = destination + ".recovery." + Guid.NewGuid().ToString("N");
            AtomicReplace(temporary, destination, displaced ?? destination + ".bak");
        }
        catch { }
        finally
        {
            TryDelete(temporary);
            if (displaced is not null) TryDelete(displaced);
        }
    }

    private static void CopyDurably(string source, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        WriteIndented = true,
    };
}
