namespace CodexBackdrop;

public enum CodexProcessRole
{
    Unknown,
    Root,
    Helper,
}

public enum CodexOwnershipFailure
{
    None,
    InvalidPackageContract,
    NoVisibleRoot,
    AmbiguousVisibleRoots,
    UnverifiableIdentity,
    RecordedRootMissingOrReused,
}

// session and user sid are part of the process generation identity.  A pid and
// creation time alone do not prove that a process still belongs to this user
// session, especially when a pid is reused by another logon or account.
public sealed record CodexProcessIdentity(
    int ProcessId,
    DateTimeOffset StartTimeUtc,
    int SessionId = -1,
    string? UserSid = null)
{
    // Alias kept explicit because the SID is read from the process owner
    // token during discovery and is often described as OwnerSid at call sites.
    public string? OwnerSid => UserSid;
}

public sealed record CodexPackageContract(
    string? PackageIdentity,
    string? PublisherIdentity,
    string? ExecutablePath);

public sealed record CodexProcessSnapshot(
    CodexProcessIdentity Identity,
    CodexProcessIdentity? ParentIdentity,
    CodexProcessRole Role,
    bool HasVisibleWindow,
    string? PackageIdentity,
    string? PublisherIdentity,
    string? ExecutablePath);

public sealed record CodexLaunchGeneration(
    CodexProcessIdentity Root,
    CodexPackageContract Package);

public sealed record CodexLaunchSelection(
    CodexLaunchGeneration? Generation,
    CodexOwnershipFailure Failure)
{
    public bool Accepted => Generation is not null && Failure == CodexOwnershipFailure.None;
}

public sealed record CodexTargetSelection(
    IReadOnlyList<CodexProcessSnapshot> Targets,
    CodexOwnershipFailure Failure)
{
    public bool Accepted => Failure == CodexOwnershipFailure.None;
}

public static class CodexProcessOwnership
{
    public static CodexLaunchSelection SelectGeneration(
        IReadOnlyList<CodexProcessSnapshot>? snapshot,
        CodexPackageContract? package)
    {
        if (!TryNormalizePackage(package, out var normalizedPackage))
            return new(null, CodexOwnershipFailure.InvalidPackageContract);

        if (snapshot is not null && !HasUniqueValidProcessIds(snapshot))
            return new(null, CodexOwnershipFailure.UnverifiableIdentity);

        if (snapshot is not null && !HasConsistentOwnerBoundary(snapshot))
            return new(null, CodexOwnershipFailure.UnverifiableIdentity);

        var visible = snapshot?.Where(process => process.HasVisibleWindow).ToArray() ?? [];
        if (visible.Length == 0)
            return new(null, CodexOwnershipFailure.NoVisibleRoot);
        if (visible.Length != 1)
            return new(null, CodexOwnershipFailure.AmbiguousVisibleRoots);

        var root = visible[0];
        if (!IsValidIdentity(root.Identity) ||
            root.Role != CodexProcessRole.Root ||
            root.ParentIdentity is not null ||
            !IsAttested(root, normalizedPackage))
            return new(null, CodexOwnershipFailure.UnverifiableIdentity);

        return new(new CodexLaunchGeneration(root.Identity, normalizedPackage), CodexOwnershipFailure.None);
    }

    public static CodexTargetSelection SelectOwnedTargets(
        CodexLaunchGeneration? generation,
        IReadOnlyList<CodexProcessSnapshot>? snapshot)
    {
        if (generation is null || !TryNormalizePackage(generation.Package, out var package) || !IsValidIdentity(generation.Root))
            return Reject(CodexOwnershipFailure.InvalidPackageContract);

        if (snapshot is null || snapshot.Count == 0)
            return Reject(CodexOwnershipFailure.RecordedRootMissingOrReused);

        var byIdentity = new Dictionary<CodexProcessIdentity, CodexProcessSnapshot>();
        var processIds = new HashSet<int>();
        foreach (var process in snapshot)
        {
            if (process is null ||
                !IsValidIdentity(process.Identity) ||
                !processIds.Add(process.Identity.ProcessId) ||
                !byIdentity.TryAdd(process.Identity, process))
                return Reject(CodexOwnershipFailure.UnverifiableIdentity);
        }

        if (!byIdentity.TryGetValue(generation.Root, out var root) ||
            root.Role != CodexProcessRole.Root ||
            root.ParentIdentity is not null ||
            !IsAttested(root, package) ||
            !SameOwnerBoundary(root.Identity, generation.Root))
            return Reject(CodexOwnershipFailure.RecordedRootMissingOrReused);

        var targets = new List<CodexProcessSnapshot> { root };
        foreach (var process in snapshot)
        {
            if (process.Identity == generation.Root) continue;
            if (!SameOwnerBoundary(process.Identity, generation.Root))
                return Reject(CodexOwnershipFailure.UnverifiableIdentity);
            if (!ReachesRecordedRoot(process, generation.Root, byIdentity, out var chain)) continue;
            if (chain.Any(ancestor =>
                    !SameOwnerBoundary(ancestor.Identity, generation.Root) ||
                    ancestor.Role != CodexProcessRole.Helper ||
                    !IsAttested(ancestor, package)))
                return Reject(CodexOwnershipFailure.UnverifiableIdentity);
            targets.Add(process);
        }

        return new(targets, CodexOwnershipFailure.None);
    }

    private static CodexTargetSelection Reject(CodexOwnershipFailure failure) => new([], failure);

    private static bool ReachesRecordedRoot(
        CodexProcessSnapshot process,
        CodexProcessIdentity root,
        IReadOnlyDictionary<CodexProcessIdentity, CodexProcessSnapshot> byIdentity,
        out IReadOnlyList<CodexProcessSnapshot> chain)
    {
        var walked = new List<CodexProcessSnapshot>();
        var seen = new HashSet<CodexProcessIdentity>();
        var current = process;
        while (current.Identity != root)
        {
            if (!seen.Add(current.Identity))
            {
                chain = [];
                return false;
            }

            walked.Add(current);
            var parent = current.ParentIdentity;
            if (parent is null ||
                parent == current.Identity ||
                current.Identity.StartTimeUtc < parent.StartTimeUtc ||
                !byIdentity.TryGetValue(parent, out current))
            {
                chain = [];
                return false;
            }
        }

        chain = walked;
        return true;
    }

    private static bool IsAttested(CodexProcessSnapshot process, CodexPackageContract package) =>
        process.Role != CodexProcessRole.Unknown &&
        string.Equals(process.PackageIdentity, package.PackageIdentity, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(process.PublisherIdentity, package.PublisherIdentity, StringComparison.OrdinalIgnoreCase) &&
        PathsEqual(process.ExecutablePath, package.ExecutablePath);

    private static bool TryNormalizePackage(CodexPackageContract? package, out CodexPackageContract normalized)
    {
        normalized = null!;
        if (package is null ||
            string.IsNullOrWhiteSpace(package.PackageIdentity) ||
            string.IsNullOrWhiteSpace(package.PublisherIdentity) ||
            !TryNormalizePath(package.ExecutablePath, out var executablePath)) return false;

        normalized = package with { ExecutablePath = executablePath };
        return true;
    }

    private static bool IsValidIdentity(CodexProcessIdentity identity) =>
        identity.ProcessId > 0 &&
        identity.StartTimeUtc != default &&
        identity.SessionId >= 0 &&
        IsValidSid(identity.UserSid);

    private static bool HasUniqueValidProcessIds(IReadOnlyList<CodexProcessSnapshot> snapshot)
    {
        var processIds = new HashSet<int>();
        foreach (var process in snapshot)
        {
            if (process is null ||
                !IsValidIdentity(process.Identity) ||
                !processIds.Add(process.Identity.ProcessId))
                return false;
        }

        return true;
    }

    private static bool HasConsistentOwnerBoundary(IReadOnlyList<CodexProcessSnapshot> snapshot)
    {
        CodexProcessIdentity? boundary = null;
        foreach (var process in snapshot)
        {
            if (process is null || !IsValidIdentity(process.Identity)) return false;
            if (boundary is null)
            {
                boundary = process.Identity;
                continue;
            }

            if (!SameOwnerBoundary(process.Identity, boundary)) return false;
        }

        return true;
    }

    private static bool SameOwnerBoundary(CodexProcessIdentity left, CodexProcessIdentity right) =>
        left.SessionId == right.SessionId &&
        string.Equals(left.UserSid, right.UserSid, StringComparison.OrdinalIgnoreCase);

    private static bool IsValidSid(string? sid)
    {
        if (string.IsNullOrWhiteSpace(sid)) return false;
        var components = sid.Split('-');
        if (components.Length < 3 || !string.Equals(components[0], "S", StringComparison.OrdinalIgnoreCase))
            return false;

        for (var index = 1; index < components.Length; index++)
        {
            if (components[index].Length == 0 ||
                !ulong.TryParse(components[index], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
                return false;
        }

        return true;
    }

    private static bool PathsEqual(string? left, string? right) =>
        TryNormalizePath(left, out var normalizedLeft) &&
        TryNormalizePath(right, out var normalizedRight) &&
        string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);

    private static bool TryNormalizePath(string? path, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        try
        {
            normalized = Path.GetFullPath(path);
            return !string.IsNullOrWhiteSpace(normalized);
        }
        catch
        {
            return false;
        }
    }
}
