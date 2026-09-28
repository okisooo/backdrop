namespace CodexBackdrop;

public sealed record AppPaths(string DataDirectory, string ConfigPath, string LogPath, string SupervisorStatePath, string RuntimeDirectory)
{
    public static AppPaths Create()
    {
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexBackdrop");
        Directory.CreateDirectory(data);
        return new AppPaths(
            data,
            Path.Combine(data, "config.json"),
            Path.Combine(data, "log.txt"),
            Path.Combine(data, "supervisor-state.json"),
            Path.Combine(AppContext.BaseDirectory, "Runtime"));
    }
}
