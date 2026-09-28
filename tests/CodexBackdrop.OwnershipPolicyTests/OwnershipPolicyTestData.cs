using CodexBackdrop;

internal static class OwnershipPolicyTestData
{
    public const int SessionId = 7;
    public const string UserSid = "S-1-5-21-100-200-300-1001";

    public static readonly string ExecutablePath = Path.GetFullPath(Path.Combine(
        Path.GetTempPath(), "OpenAI.Codex_26.825.3734.0_x64__testpublisher", "app", "ChatGPT.exe"));

    public static readonly CodexPackageContract Package = new(
        "OpenAI.Codex_26.825.3734.0_x64__testpublisher",
        "CN=OpenAI Corporation",
        ExecutablePath);

    public static CodexProcessIdentity Identity(
        int processId,
        int seconds,
        int sessionId = SessionId,
        string? userSid = UserSid) =>
        new(processId, DateTimeOffset.Parse($"2026-08-29T00:00:{seconds:00}Z"), sessionId, userSid);

    public static CodexProcessSnapshot Root(
        int processId = 10,
        int startSeconds = 10,
        bool visible = true,
        CodexProcessRole role = CodexProcessRole.Root,
        string? package = null,
        string? publisher = null,
        string? executable = null,
        int sessionId = SessionId,
        string? userSid = UserSid) =>
        new(
            Identity(processId, startSeconds, sessionId, userSid),
            null,
            role,
            visible,
            package ?? Package.PackageIdentity,
            publisher ?? Package.PublisherIdentity,
            executable ?? Package.ExecutablePath);

    public static CodexProcessSnapshot Helper(
        int processId,
        CodexProcessIdentity parent,
        int startSeconds = 20,
        bool visible = false,
        CodexProcessRole role = CodexProcessRole.Helper,
        string? package = null,
        string? publisher = null,
        string? executable = null,
        int sessionId = SessionId,
        string? userSid = UserSid) =>
        new(
            Identity(processId, startSeconds, sessionId, userSid),
            parent,
            role,
            visible,
            package ?? Package.PackageIdentity,
            publisher ?? Package.PublisherIdentity,
            executable ?? Package.ExecutablePath);

    public static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static void ExpectFailure(CodexOwnershipFailure expected, CodexLaunchSelection actual, string name)
    {
        Expect(!actual.Accepted, $"{name}: expected rejection");
        Expect(actual.Failure == expected, $"{name}: expected {expected}, got {actual.Failure}");
    }

    public static void ExpectFailure(CodexOwnershipFailure expected, CodexTargetSelection actual, string name)
    {
        Expect(!actual.Accepted, $"{name}: expected rejection");
        Expect(actual.Failure == expected, $"{name}: expected {expected}, got {actual.Failure}");
        Expect(actual.Targets.Count == 0, $"{name}: rejected selection returned destructive targets");
    }
}
