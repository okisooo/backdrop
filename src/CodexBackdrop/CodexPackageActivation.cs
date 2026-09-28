using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;

namespace CodexBackdrop;

/// <summary>
/// Starts the official Codex app through Windows package activation. Since
/// Codex 26.924 the app refuses to bootstrap without package identity, and a
/// process started from the executable path no longer receives one.
/// Activation keeps the identity, passes the arguments through, and reports
/// the exact process it created.
/// </summary>
internal static class CodexPackageActivation
{
    private const int NoErrorUi = 0x2;
    private const int ErrorInsufficientBuffer = 122;
    private static readonly XNamespace ManifestNamespace = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";

    /// <summary>
    /// Returns ChatGPT.exe of the newest official Codex package registered for
    /// this user, straight from the package APIs rather than a PowerShell query.
    /// </summary>
    public static string FindExecutable()
    {
        uint count = 0;
        uint bufferLength = 0;
        var result = GetPackagesByPackageFamily(WindowsCodexDiscovery.OfficialPackageFamilyName, ref count, null, ref bufferLength, null);
        if (result == 0 && count == 0)
            throw new FileNotFoundException("codex is not installed for this user");
        if (result != ErrorInsufficientBuffer)
            throw new InvalidOperationException($"the codex package could not be listed (win32={result})");

        var names = new IntPtr[count];
        var buffer = new char[bufferLength];
        result = GetPackagesByPackageFamily(WindowsCodexDiscovery.OfficialPackageFamilyName, ref count, names, ref bufferLength, buffer);
        if (result != 0)
            throw new InvalidOperationException($"the codex package could not be listed (win32={result})");

        var newest = new string(buffer, 0, (int)bufferLength)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(fullName => (FullName: fullName, Version: Version.TryParse(fullName.Split('_').ElementAtOrDefault(1), out var version) ? version : null))
            .Where(package => package.Version is not null)
            .OrderByDescending(package => package.Version)
            .Select(package => package.FullName)
            .FirstOrDefault()
            ?? throw new FileNotFoundException("the official codex package could not be verified");

        uint pathLength = 0;
        result = GetPackagePathByFullName(newest, ref pathLength, null);
        if (result != ErrorInsufficientBuffer)
            throw new InvalidOperationException($"the codex install folder could not be resolved (win32={result})");
        var path = new StringBuilder((int)pathLength);
        result = GetPackagePathByFullName(newest, ref pathLength, path);
        if (result != 0)
            throw new InvalidOperationException($"the codex install folder could not be resolved (win32={result})");

        var executable = Path.Combine(path.ToString(), "app", "ChatGPT.exe");
        if (!File.Exists(executable) || !WindowsCodexDiscovery.IsExpectedPackageExecutable(executable))
            throw new FileNotFoundException("the official codex app could not be verified");
        return Path.GetFullPath(executable);
    }

    public static int Activate(string executable, IEnumerable<string> arguments)
    {
        var appUserModelId = ResolveAppUserModelId(executable);
        var commandLine = string.Join(' ', arguments.Select(QuoteArgument));
        var manager = (IApplicationActivationManager)new ApplicationActivationManager();
        try
        {
            var result = manager.ActivateApplication(appUserModelId, commandLine, NoErrorUi, out var processId);
            if (result < 0 || processId == 0)
                throw new InvalidOperationException($"windows refused to start codex (0x{result:X8})");
            return checked((int)processId);
        }
        finally
        {
            Marshal.FinalReleaseComObject(manager);
        }
    }

    // Quotes one argument the way CommandLineToArgvW reads it back.
    private static string QuoteArgument(string value)
    {
        if (value.Length > 0 && value.All(character => !char.IsWhiteSpace(character) && character != '"'))
            return value;

        var result = new StringBuilder(value.Length + 2).Append('"');
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                slashes++;
                continue;
            }
            if (character == '"')
            {
                result.Append('\\', slashes * 2 + 1).Append('"');
                slashes = 0;
                continue;
            }
            result.Append('\\', slashes).Append(character);
            slashes = 0;
        }
        result.Append('\\', slashes * 2).Append('"');
        return result.ToString();
    }

    // The package manifest names the application entry that runs ChatGPT.exe;
    // resolving it there keeps activation tied to the executable that the
    // ownership checks attest afterwards.
    private static string ResolveAppUserModelId(string executable)
    {
        var packageDirectory = Directory.GetParent(executable)?.Parent
            ?? throw new InvalidOperationException("the official codex package could not be located");
        var relativeExecutable = Path.GetRelativePath(packageDirectory.FullName, executable).Replace('\\', '/');
        var manifest = XDocument.Load(Path.Combine(packageDirectory.FullName, "AppxManifest.xml"));
        var applicationId = manifest
            .Descendants(ManifestNamespace + "Application")
            .FirstOrDefault(application => string.Equals(
                application.Attribute("Executable")?.Value.Replace('\\', '/'),
                relativeExecutable,
                StringComparison.OrdinalIgnoreCase))
            ?.Attribute("Id")?.Value;
        if (string.IsNullOrWhiteSpace(applicationId))
            throw new InvalidOperationException("the official codex app entry could not be verified");
        return $"{WindowsCodexDiscovery.OfficialPackageFamilyName}!{applicationId}";
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagesByPackageFamily(
        string packageFamilyName,
        ref uint count,
        [Out] IntPtr[]? packageFullNames,
        ref uint bufferLength,
        [Out] char[]? buffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagePathByFullName(string packageFullName, ref uint pathLength, StringBuilder? path);

    [ComImport]
    [Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments,
            int options,
            out uint processId);
    }

    [ComImport]
    [Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager;
}
