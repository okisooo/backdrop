using System.Text;

namespace CodexBackdrop;

/// <summary>
/// The single append path for the diagnostic log. The injector, supervisor
/// and controller handoff can log at the same moment, sometimes from two
/// processes; independent appends collided and silently dropped lines.
/// </summary>
internal static class BackdropLog
{
    private const long RotationBytes = 2 * 1024 * 1024;
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    public static void Append(string path, string message)
    {
        try
        {
            using var mutex = new Mutex(false, @"Local\CodexBackdrop.Log");
            var owned = false;
            try
            {
                try { owned = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
                catch (AbandonedMutexException) { owned = true; }

                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                if (File.Exists(path) && new FileInfo(path).Length > RotationBytes)
                    File.Move(path, path + ".1", true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}", Utf8);
            }
            finally
            {
                if (owned) mutex.ReleaseMutex();
            }
        }
        catch { }
    }
}
