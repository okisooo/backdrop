using System.Diagnostics;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace CodexBackdrop;

internal sealed record CodexProcessInventory(
    IReadOnlyList<CodexProcessSnapshot> Processes,
    bool HasUnverifiableCodexCandidate);

internal static class WindowsCodexDiscovery
{
    internal const string OfficialPackageFamilyName = "OpenAI.Codex_2p2nqsd0c76g0";

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessTerminate = 0x0001;
    private const uint ProcessSynchronize = 0x00100000;
    private const uint SnapshotProcesses = 0x00000002;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorNoMoreFiles = 18;
    private const int AddressFamilyInet = 2;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    public static CodexProcessInventory Capture()
    {
        if (!TryGetCurrentSessionId(out var currentSessionId))
            return new([], true);

        // A missing current SID does not make foreign-session processes unsafe:
        // they are discarded before any path/package work. Same-session
        // candidates remain unverifiable until their owner SID can be proved.
        TryGetCurrentUserSid(out var currentUserSid);
        var processTable = CaptureProcessTable(currentSessionId);
        var facts = new List<ProcessFacts>();
        var unverifiable = !processTable.IsComplete;
        var unverifiableProcessIds = new HashSet<int>();
        var enumeratedProcessIds = new HashSet<int>();

        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName("ChatGPT");
        }
        catch
        {
            return new([], true);
        }

        foreach (var process in processes)
        {
            int? processId = null;
            try
            {
                try { processId = process.Id; }
                catch { }
                if (processId is int enumeratedPid) enumeratedProcessIds.Add(enumeratedPid);
                else unverifiable = true;

                if (processId is not int candidatePid ||
                    !TryGetProcessSessionId(candidatePid, out var processSessionId))
                {
                    unverifiable = true;
                    if (processId is int sessionFailedPid) unverifiableProcessIds.Add(sessionFailedPid);
                    continue;
                }

                // Session is a cheap, independent identity boundary. A
                // proven foreign-session process cannot be our Codex and must
                // not trigger expensive module, package, or token inspection.
                if (processSessionId != currentSessionId) continue;

                var result = TryCaptureFacts(candidatePid, currentSessionId, currentUserSid, out var captured);
                if (result == ProcessCaptureResult.Captured)
                {
                    facts.Add(captured);
                }
                else if (result == ProcessCaptureResult.Unverifiable)
                {
                    unverifiable = true;
                    if (processId is int failedPid) unverifiableProcessIds.Add(failedPid);
                }
            }
            catch
            {
                unverifiable = true;
                if (processId is int caughtPid) unverifiableProcessIds.Add(caughtPid);
            }
            finally
            {
                process.Dispose();
            }
        }

        if (processTable.IsComplete && processTable.Entries.Any(entry =>
                entry.Value.IsChatGptProcess && !enumeratedProcessIds.Contains(entry.Key)))
            unverifiable = true;

        var byProcessId = new Dictionary<int, ProcessFacts>();
        foreach (var fact in facts)
        {
            if (!byProcessId.TryAdd(fact.Identity.ProcessId, fact))
            {
                // A duplicate pid means the process list changed while it was
                // being captured. Do not let either identity become a target.
                byProcessId.Remove(fact.Identity.ProcessId);
                unverifiable = true;
            }
        }

        var snapshots = new List<CodexProcessSnapshot>(byProcessId.Count);
        foreach (var fact in byProcessId.Values)
        {
            CodexProcessIdentity? parentIdentity = null;
            var role = CodexProcessRole.Root;
            if (!processTable.IsComplete ||
                !processTable.Entries.TryGetValue(fact.Identity.ProcessId, out var processEntry) ||
                processEntry.ParentProcessId <= 0 ||
                processEntry.ParentProcessId == fact.Identity.ProcessId)
            {
                role = CodexProcessRole.Unknown;
                unverifiable = true;
            }
            else if (unverifiableProcessIds.Contains(processEntry.ParentProcessId))
            {
                role = CodexProcessRole.Unknown;
                unverifiable = true;
            }
            else if (byProcessId.TryGetValue(processEntry.ParentProcessId, out var parent))
            {
                if (fact.Identity.StartTimeUtc < parent.Identity.StartTimeUtc)
                {
                    // The parent pid now names a process that started after
                    // this child. Treat the relationship as stale/reused.
                    role = CodexProcessRole.Unknown;
                    unverifiable = true;
                }
                else
                {
                    parentIdentity = parent.Identity;
                    role = CodexProcessRole.Helper;
                }
            }
            else if (!processTable.Entries.TryGetValue(processEntry.ParentProcessId, out var parentEntry))
            {
                // A launcher or prior controller may exit while the official
                // Codex window remains alive. The already attested, visible
                // process is still a safe root candidate; an invisible orphan
                // could instead be a detached helper and remains fail-closed.
                if (!fact.HasVisibleWindow)
                {
                    role = CodexProcessRole.Unknown;
                    unverifiable = true;
                }
            }
            else if (!parentEntry.HasKnownExecutableName || parentEntry.IsChatGptProcess)
            {
                // An unnamed parent cannot prove that this process is a root;
                // a ChatGPT parent that was not captured is unsafe.
                role = CodexProcessRole.Unknown;
                unverifiable = true;
            }

            snapshots.Add(new CodexProcessSnapshot(
                fact.Identity,
                parentIdentity,
                role,
                fact.HasVisibleWindow,
                fact.PackageIdentity,
                fact.PackageFamilyName,
                fact.ExecutablePath));
        }

        return new(snapshots, unverifiable);
    }

    public static bool TryOpenExact(
        CodexProcessIdentity identity,
        CodexPackageContract package,
        out CodexProcessLease? lease)
    {
        lease = null;
        if (package is null ||
            identity.ProcessId <= 0 ||
            identity.StartTimeUtc == default ||
            identity.SessionId < 0 ||
            string.IsNullOrWhiteSpace(identity.UserSid) ||
            !TryGetCurrentSessionId(out var currentSessionId) ||
            identity.SessionId != currentSessionId ||
            !TryGetCurrentUserSid(out var currentUserSid) ||
            !SidEquals(identity.UserSid, currentUserSid)) return false;

        var nativeHandle = IntPtr.Zero;
        try
        {
            // The handle is opened before any identity revalidation and is
            // retained by the lease. Subsequent close/terminate actions use
            // this same native object, never a fresh lookup by pid.
            nativeHandle = OpenProcess(
                ProcessQueryLimitedInformation | ProcessTerminate | ProcessSynchronize,
                false,
                identity.ProcessId);
            if (nativeHandle == IntPtr.Zero ||
                TryCaptureFacts(identity.ProcessId, currentSessionId, currentUserSid, nativeHandle, out var current) != ProcessCaptureResult.Captured ||
                current.Identity != identity ||
                !string.Equals(current.PackageIdentity, package.PackageIdentity, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(current.PackageFamilyName, package.PublisherIdentity, StringComparison.OrdinalIgnoreCase) ||
                !PathsEqual(current.ExecutablePath, package.ExecutablePath))
            {
                return false;
            }

            lease = new CodexProcessLease(nativeHandle, current.Identity, current.MainWindowHandle);
            nativeHandle = IntPtr.Zero;
            return true;
        }
        catch
        {
            lease?.Dispose();
            lease = null;
            return false;
        }
        finally
        {
            if (nativeHandle != IntPtr.Zero) CloseHandle(nativeHandle);
        }
    }

    /// <summary>
    /// Captures a launch process identity from one pinned native handle. This
    /// is useful immediately after <see cref="Process.Start"/>: callers can
    /// record the session and owner SID without using the managed Process
    /// object's pid/start-time pair, while later package/path attestation is
    /// still performed by <see cref="Capture"/> and <see cref="TryOpenExact"/>.
    /// </summary>
    internal static bool TryCaptureIdentity(int processId, out CodexProcessIdentity? identity)
    {
        identity = null;
        if (processId <= 0 ||
            !TryGetCurrentSessionId(out var currentSessionId) ||
            !TryGetCurrentUserSid(out var currentUserSid) ||
            !TryGetProcessSessionId(processId, out var processSessionId) ||
            processSessionId != currentSessionId ||
            string.IsNullOrWhiteSpace(currentUserSid)) return false;

        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return false;
        try
        {
            if (!TryGetProcessOwnerSid(handle, out var ownerSid) ||
                !SidEquals(ownerSid, currentUserSid) ||
                GetProcessId(handle) != (uint)processId ||
                !TryGetProcessSessionId(processId, out var handleSessionId) ||
                handleSessionId != currentSessionId ||
                !TryGetProcessStartTime(handle, out var startTime)) return false;

            identity = new CodexProcessIdentity(processId, startTime, currentSessionId, currentUserSid);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static bool IsGenerationRunning(CodexLaunchGeneration generation) =>
        TryOpenExact(generation.Root, generation.Package, out var lease) && DisposeAndReturn(lease, true);

    public static bool TryGetExclusiveLoopbackListenerOwner(int port, out int processId)
    {
        processId = 0;
        if (port is < 1 or > 65535) return false;

        var bufferLength = 0;
        var result = GetExtendedTcpTable(
            IntPtr.Zero,
            ref bufferLength,
            true,
            AddressFamilyInet,
            TcpTableClass.OwnerPidListener,
            0);
        if (result != ErrorInsufficientBuffer || bufferLength <= sizeof(int)) return false;

        var buffer = Marshal.AllocHGlobal(bufferLength);
        try
        {
            result = GetExtendedTcpTable(
                buffer,
                ref bufferLength,
                true,
                AddressFamilyInet,
                TcpTableClass.OwnerPidListener,
                0);
            if (result != 0) return false;

            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            var rowPointer = IntPtr.Add(buffer, sizeof(int));
            var owners = new HashSet<int>();
            for (var index = 0; index < count; index++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPointer);
                rowPointer = IntPtr.Add(rowPointer, rowSize);
                if (NetworkPort(row.LocalPort) != port) continue;

                var address = new IPAddress(BitConverter.GetBytes(row.LocalAddress));
                if (!IPAddress.IsLoopback(address)) return false;
                owners.Add(checked((int)row.OwningProcessId));
            }

            if (owners.Count != 1) return false;
            processId = owners.Single();
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static bool IsExpectedPackageExecutable(string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable)) return false;
        try
        {
            var fullPath = Path.GetFullPath(executable);
            if (!string.Equals(Path.GetFileName(fullPath), "ChatGPT.exe", StringComparison.OrdinalIgnoreCase)) return false;
            var appDirectory = Directory.GetParent(fullPath);
            var packageDirectory = appDirectory?.Parent;
            return appDirectory is not null &&
                packageDirectory is not null &&
                string.Equals(appDirectory.Name, "app", StringComparison.OrdinalIgnoreCase) &&
                packageDirectory.Name.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) &&
                packageDirectory.Name.EndsWith("__2p2nqsd0c76g0", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static ProcessCaptureResult TryCaptureFacts(
        int processId,
        int currentSessionId,
        string? currentUserSid,
        out ProcessFacts facts) =>
        TryCaptureFacts(processId, currentSessionId, currentUserSid, IntPtr.Zero, out facts);

    private static ProcessCaptureResult TryCaptureFacts(
        int processId,
        int currentSessionId,
        string? currentUserSid,
        IntPtr suppliedHandle,
        out ProcessFacts facts)
    {
        facts = null!;
        var ownsHandle = suppliedHandle == IntPtr.Zero;
        var handle = suppliedHandle;
        try
        {
            if (processId <= 0 ||
                !TryGetProcessSessionId(processId, out var processSessionId))
                return ProcessCaptureResult.Unverifiable;
            if (processSessionId != currentSessionId)
                return ProcessCaptureResult.ProvenUnrelated;
            if (string.IsNullOrWhiteSpace(currentUserSid))
                return ProcessCaptureResult.Unverifiable;

            if (handle == IntPtr.Zero)
                handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (handle == IntPtr.Zero) return ProcessCaptureResult.Unverifiable;

            // Recheck the identity boundary from the opened handle as well as
            // from the initial pid query. If the pid was reused between those
            // operations, this describes the process actually held by the
            // handle rather than the stale enumerated process.
            var handleProcessId = GetProcessId(handle);
            if (
                handleProcessId == 0 ||
                handleProcessId != (uint)processId ||
                !TryGetProcessSessionId(processId, out processSessionId))
                return ProcessCaptureResult.Unverifiable;
            if (processSessionId != currentSessionId)
                return ProcessCaptureResult.ProvenUnrelated;

            // Owner SID is checked before the executable path and package
            // checks. A same-session process owned by another SID, or a token
            // that cannot be read, is not treated as unrelated: the inventory
            // becomes unverifiable and all automatic control is refused.
            if (!TryGetProcessOwnerSid(handle, out var ownerSid) ||
                !SidEquals(ownerSid, currentUserSid))
                return ProcessCaptureResult.Unverifiable;

            var executable = TryGetProcessPath(handle);
            if (!IsExpectedPackageExecutable(executable))
            {
                return !IsKnownAbsolutePath(executable)
                    ? ProcessCaptureResult.Unverifiable
                    : ProcessCaptureResult.ProvenUnrelated;
            }

            var packageIdentity = QueryPackageString(handle, GetPackageFullName);
            var packageFamilyName = QueryPackageString(handle, GetPackageFamilyName);
            if (string.IsNullOrWhiteSpace(packageIdentity) || string.IsNullOrWhiteSpace(packageFamilyName))
                return ProcessCaptureResult.Unverifiable;
            if (!string.Equals(packageFamilyName, OfficialPackageFamilyName, StringComparison.OrdinalIgnoreCase) ||
                !PackagePathMatches(executable!, packageIdentity))
                return ProcessCaptureResult.ProvenUnrelated;

            if (!TryGetProcessStartTime(handle, out var startTime))
                return ProcessCaptureResult.Unverifiable;
            if (!TryGetMainWindowHandle(processId, out var mainWindowHandle))
                return ProcessCaptureResult.Unverifiable;

            facts = new ProcessFacts(
                new CodexProcessIdentity(processId, startTime, currentSessionId, currentUserSid),
                packageIdentity,
                packageFamilyName!,
                Path.GetFullPath(executable!),
                mainWindowHandle != IntPtr.Zero,
                mainWindowHandle);
            return ProcessCaptureResult.Captured;
        }
        catch
        {
            return ProcessCaptureResult.Unverifiable;
        }
        finally
        {
            if (ownsHandle && handle != IntPtr.Zero) CloseHandle(handle);
        }
    }

    private static string? QueryPackageString(IntPtr processHandle, PackageStringQuery query)
    {
        uint length = 0;
        var result = query(processHandle, ref length, null);
        if (result != ErrorInsufficientBuffer || length == 0) return null;
        var buffer = new StringBuilder(checked((int)length));
        return query(processHandle, ref length, buffer) == 0 ? buffer.ToString() : null;
    }

    private static bool PackagePathMatches(string executable, string packageIdentity)
    {
        try
        {
            var packageDirectory = Directory.GetParent(executable)?.Parent;
            return packageDirectory is not null &&
                string.Equals(packageDirectory.Name, packageIdentity, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsKnownAbsolutePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        try
        {
            return !string.IsNullOrWhiteSpace(Path.GetFullPath(path));
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryGetMainWindowHandle(int processId, out IntPtr mainWindowHandle)
    {
        mainWindowHandle = IntPtr.Zero;
        if (processId <= 0) return false;
        try
        {
            var foundWindow = IntPtr.Zero;
            var enumerated = EnumWindows(
                (window, _) =>
                {
                    if (!IsWindowVisible(window) ||
                        GetWindowThreadProcessId(window, out var ownerProcessId) == 0 ||
                        ownerProcessId != (uint)processId) return true;

                    foundWindow = window;
                    return true;
                },
                IntPtr.Zero);
            mainWindowHandle = foundWindow;
            return enumerated;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryGetProcessPath(IntPtr processHandle)
    {
        try
        {
            var capacity = 32768u;
            var buffer = new StringBuilder(checked((int)capacity));
            if (!QueryFullProcessImageName(processHandle, 0, buffer, ref capacity)) return null;
            var path = buffer.ToString();
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetCurrentSessionId(out int sessionId)
    {
        sessionId = -1;
        try
        {
            if (!ProcessIdToSessionId(GetCurrentProcessId(), out var nativeSessionId)) return false;
            sessionId = checked((int)nativeSessionId);
            return sessionId >= 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetProcessSessionId(int processId, out int sessionId)
    {
        sessionId = -1;
        if (processId <= 0) return false;
        try
        {
            if (!ProcessIdToSessionId(checked((uint)processId), out var nativeSessionId)) return false;
            sessionId = checked((int)nativeSessionId);
            return sessionId >= 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetCurrentUserSid(out string? userSid)
    {
        userSid = null;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            userSid = identity.User?.Value;
            return !string.IsNullOrWhiteSpace(userSid);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetProcessOwnerSid(IntPtr processHandle, out string? userSid)
    {
        userSid = null;
        if (processHandle == IntPtr.Zero || !OpenProcessToken(processHandle, TokenQuery, out var tokenHandle))
            return false;

        var buffer = IntPtr.Zero;
        try
        {
            GetTokenInformation(tokenHandle, TokenInformationClass.TokenUser, IntPtr.Zero, 0, out var length);
            if (length == 0) return false;
            buffer = Marshal.AllocHGlobal(checked((int)length));
            if (!GetTokenInformation(tokenHandle, TokenInformationClass.TokenUser, buffer, length, out _))
                return false;

            var tokenUser = Marshal.PtrToStructure<TokenUser>(buffer);
            if (tokenUser.User.Sid == IntPtr.Zero) return false;
            userSid = new SecurityIdentifier(tokenUser.User.Sid).Value;
            return !string.IsNullOrWhiteSpace(userSid);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            CloseHandle(tokenHandle);
        }
    }

    private static bool TryGetProcessStartTime(IntPtr processHandle, out DateTimeOffset startTimeUtc)
    {
        startTimeUtc = default;
        try
        {
            if (!GetProcessTimes(
                    processHandle,
                    out var creationTime,
                    out _,
                    out _,
                    out _) ||
                creationTime.dwHighDateTime == 0 && creationTime.dwLowDateTime == 0)
                return false;

            var fileTime = ((long)creationTime.dwHighDateTime << 32) | creationTime.dwLowDateTime;
            startTimeUtc = new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime), TimeSpan.Zero);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool SidEquals(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static ProcessTableSnapshot CaptureProcessTable(int currentSessionId)
    {
        var entries = new Dictionary<int, ProcessTableEntry>();
        IntPtr snapshot;
        try { snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0); }
        catch { return new(entries, false); }
        if (snapshot == InvalidHandleValue) return new(entries, false);

        try
        {
            var entry = new ProcessEntry32 { Size = checked((uint)Marshal.SizeOf<ProcessEntry32>()) };
            if (!Process32First(snapshot, ref entry))
                return new(entries, IsProcessEnumerationComplete());

            do
            {
                var processId = checked((int)entry.ProcessId);
                var parentProcessId = checked((int)entry.ParentProcessId);
                var isChatGptProcess = string.Equals(
                    Path.GetFileName(entry.ExecutableFile),
                    "ChatGPT.exe",
                    StringComparison.OrdinalIgnoreCase);
                if (isChatGptProcess &&
                    TryGetProcessSessionId(processId, out var processSessionId) &&
                    processSessionId != currentSessionId)
                {
                    // Toolhelp can see ChatGPT processes from other logon
                    // sessions. They are not candidates for this instance and
                    // must not make an otherwise complete current-session
                    // inventory unverifiable.
                    isChatGptProcess = false;
                }

                if (!entries.TryAdd(processId, new ProcessTableEntry(
                        parentProcessId,
                        !string.IsNullOrWhiteSpace(entry.ExecutableFile),
                        isChatGptProcess)))
                    return new(entries, false);

                entry.Size = checked((uint)Marshal.SizeOf<ProcessEntry32>());
            }
            while (Process32Next(snapshot, ref entry));
            return new(entries, IsProcessEnumerationComplete());
        }
        catch
        {
            return new(entries, false);
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    private static bool IsProcessEnumerationComplete() =>
        Marshal.GetLastWin32Error() is 0 or ErrorNoMoreFiles;

    private static bool PathsEqual(string? left, string? right)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(left) &&
                !string.IsNullOrWhiteSpace(right) &&
                string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static int NetworkPort(uint value) =>
        unchecked((ushort)IPAddress.NetworkToHostOrder(unchecked((short)(value & 0xffff))));

    private static bool DisposeAndReturn(CodexProcessLease? lease, bool value)
    {
        lease?.Dispose();
        return value;
    }

    private enum ProcessCaptureResult
    {
        Captured,
        ProvenUnrelated,
        Unverifiable,
    }

    private sealed record ProcessTableSnapshot(
        IReadOnlyDictionary<int, ProcessTableEntry> Entries,
        bool IsComplete);

    private sealed record ProcessTableEntry(
        int ParentProcessId,
        bool HasKnownExecutableName,
        bool IsChatGptProcess);

    private sealed record ProcessFacts(
        CodexProcessIdentity Identity,
        string PackageIdentity,
        string PackageFamilyName,
        string ExecutablePath,
        bool HasVisibleWindow,
        IntPtr MainWindowHandle);

    private const uint TokenQuery = 0x0008;

    private enum TokenInformationClass
    {
        TokenUser = 1,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenUser
    {
        public SidAndAttributes User;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    private delegate int PackageStringQuery(IntPtr processHandle, ref uint length, StringBuilder? value);

    private enum TcpTableClass
    {
        OwnerPidListener = 3,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint OwningProcessId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessId(IntPtr process);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFullName(IntPtr process, ref uint packageFullNameLength, StringBuilder? packageFullName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFamilyName(IntPtr process, ref uint packageFamilyNameLength, StringBuilder? packageFamilyName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        IntPtr process,
        out NativeFileTime creationTime,
        out NativeFileTime exitTime,
        out NativeFileTime kernelTime,
        out NativeFileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr lParam);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        TokenInformationClass tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedTcpTable(
        IntPtr table,
        ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily,
        TcpTableClass tableClass,
        uint reserved);
}

/// <summary>
/// An exact process lease. The native process handle is opened before the
/// identity is revalidated and remains the only process-control primitive for
/// the lifetime of the lease. This prevents a pid-reuse gap between validation
/// and graceful-close/termination actions.
/// </summary>
public sealed class CodexProcessLease : IDisposable
{
    private const uint StillActive = 259;
    private const uint WaitObject = 0;
    private const uint WaitTimeout = 258;
    private const uint Infinite = 0xffffffff;
    private const uint WindowCloseMessage = 0x0010;

    private IntPtr _nativeHandle;
    private readonly IntPtr _mainWindowHandle;

    internal CodexProcessLease(IntPtr nativeHandle, CodexProcessIdentity identity, IntPtr mainWindowHandle)
    {
        if (nativeHandle == IntPtr.Zero) throw new ArgumentException("a native process handle is required", nameof(nativeHandle));
        Identity = identity;
        _nativeHandle = nativeHandle;
        _mainWindowHandle = mainWindowHandle;
    }

    public CodexProcessIdentity Identity { get; }

    // Keep the Process-shaped surface used by the current RuntimeManager while
    // making the underlying operation handle-pinned rather than pid-reopened.
    public int Id => Identity.ProcessId;

    public bool HasExited
    {
        get
        {
            var handle = GetHandle();
            return !GetExitCodeProcess(handle, out var exitCode) || exitCode != StillActive;
        }
    }

    /// <summary>
    /// Sends WM_CLOSE to the exact lease's currently visible top-level window.
    /// The process handle is checked immediately before the message is sent;
    /// termination remains available through <see cref="Kill"/> on that same
    /// handle if graceful shutdown does not complete.
    /// </summary>
    public bool CloseMainWindow()
    {
        var handle = GetHandle();
        EnsureRunning(handle);
        var window = _mainWindowHandle;
        if (window == IntPtr.Zero ||
            !IsWindowVisible(window) ||
            GetWindowThreadProcessId(window, out var ownerProcessId) == 0 ||
            ownerProcessId != (uint)Id)
            return false;
        EnsureRunning(handle);
        return PostMessage(window, WindowCloseMessage, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Terminates the exact process represented by this lease.</summary>
    public void Kill()
    {
        var handle = GetHandle();
        EnsureRunning(handle);
        if (TerminateProcess(handle, 1)) return;

        var terminationError = Marshal.GetLastWin32Error();

        // A process can exit between the status check and TerminateProcess.
        // Report that race as the same benign condition as Process.Kill does;
        // never reopen the pid to decide what to terminate.
        if (!GetExitCodeProcess(handle, out var exitCode) || exitCode != StillActive)
            throw new InvalidOperationException("the exact process exited before termination");

        throw new Win32Exception(terminationError, "the exact process could not be terminated");
    }

    public bool WaitForExit(TimeSpan timeout)
    {
        var handle = GetHandle();
        var waitMilliseconds = ToWaitMilliseconds(timeout);
        var result = WaitForSingleObject(handle, waitMilliseconds);
        if (result == WaitObject) return true;
        if (result == WaitTimeout) return false;
        throw new Win32Exception(Marshal.GetLastWin32Error(), "waiting for the exact process failed");
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _nativeHandle, IntPtr.Zero);
        if (handle != IntPtr.Zero) CloseHandle(handle);
        GC.SuppressFinalize(this);
    }

    ~CodexProcessLease() => Dispose();

    private IntPtr GetHandle()
    {
        var handle = Volatile.Read(ref _nativeHandle);
        return handle == IntPtr.Zero
            ? throw new ObjectDisposedException(nameof(CodexProcessLease))
            : handle;
    }

    private static void EnsureRunning(IntPtr handle)
    {
        if (!GetExitCodeProcess(handle, out var exitCode))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "the exact process status could not be verified");
        if (exitCode != StillActive)
            throw new InvalidOperationException("the exact process has already exited");
    }

    private static uint ToWaitMilliseconds(TimeSpan timeout)
    {
        if (timeout == Timeout.InfiniteTimeSpan) return Infinite;
        if (timeout <= TimeSpan.Zero) return 0;
        var milliseconds = timeout.TotalMilliseconds;
        return milliseconds >= uint.MaxValue ? uint.MaxValue : checked((uint)milliseconds);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
