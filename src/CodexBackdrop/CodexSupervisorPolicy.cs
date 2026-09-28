namespace CodexBackdrop;

public enum SupervisorAction
{
    None,
    Attach,
    Handoff,
}

public sealed record CodexObservation(
    bool IsRunning,
    string? GenerationKey,
    string? PackageIdentity,
    int? MainProcessId,
    bool DevToolsAvailable,
    bool InjectorHealthy,
    DateTimeOffset? MainProcessStartTimeUtc = null,
    string? ExecutablePath = null,
    string? PackageFamilyName = null,
    string? SafetyFailure = null,
    bool InjectorRunning = false);

public sealed record SupervisorDecision(SupervisorAction Action, string Status, bool Connected = false);

public sealed class SupervisorPersistentState
{
    public string? BlockedPackageIdentity { get; set; }
    public string? LastAttemptedGeneration { get; set; }
    public string? LastFailure { get; set; }
    public bool FailureRecorded { get; set; }
    public List<string> RestartProtectedGenerations { get; set; } = [];
}

public sealed class CodexSupervisorPolicy
{
    private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(1200);
    private string? _observedGeneration;
    private DateTimeOffset _observedSince;

    public CodexSupervisorPolicy(SupervisorPersistentState state)
    {
        State = state;
        State.RestartProtectedGenerations ??= [];
    }

    public SupervisorPersistentState State { get; }

    public SupervisorDecision Decide(CodexObservation observation, bool enabled, DateTimeOffset now, bool force = false)
    {
        if (!enabled) return new(SupervisorAction.None, "automatic attachment paused");
        if (!observation.IsRunning)
        {
            _observedGeneration = null;
            return new(SupervisorAction.None, "watching for codex");
        }

        if (string.IsNullOrWhiteSpace(observation.GenerationKey) ||
            string.IsNullOrWhiteSpace(observation.PackageIdentity) ||
            observation.MainProcessId is null ||
            observation.MainProcessStartTimeUtc is null ||
            string.IsNullOrWhiteSpace(observation.ExecutablePath) ||
            string.IsNullOrWhiteSpace(observation.PackageFamilyName))
        {
            _observedGeneration = null;
            return new(SupervisorAction.None, observation.SafetyFailure ?? "codex identity is ambiguous • no automatic action");
        }

        if (observation.InjectorHealthy)
            return new(SupervisorAction.None, "backdrop attached", true);

        // ponytail: the worker already retries renderer checks; do not restart it during a slow poll.
        if (!force && observation.InjectorRunning)
            return new(SupervisorAction.None, "waiting for backdrop renderer • codex left open");

        if (observation.DevToolsAvailable)
        {
            if (!force && State.FailureRecorded && State.BlockedPackageIdentity == observation.PackageIdentity)
                return new(SupervisorAction.None, State.LastFailure ?? "automatic retry stopped for this codex version");
            if (!force && State.LastAttemptedGeneration == observation.GenerationKey)
                return new(SupervisorAction.None, State.LastFailure ?? "attachment needs attention");
            return new(SupervisorAction.Attach, "connecting backdrop");
        }

        if (!force && observation.GenerationKey is not null && State.RestartProtectedGenerations.Contains(observation.GenerationKey))
            return new(SupervisorAction.None, "automatic restart stopped for this codex launch");

        if (!force && State.BlockedPackageIdentity == observation.PackageIdentity)
            return new(SupervisorAction.None, State.LastFailure ?? "automatic retry stopped for this codex version");

        if (!force && State.LastAttemptedGeneration == observation.GenerationKey)
            return new(SupervisorAction.None, State.LastFailure ?? "automatic retry stopped for this codex launch");

        if (_observedGeneration != observation.GenerationKey)
        {
            _observedGeneration = observation.GenerationKey;
            _observedSince = now;
        }

        if (!force && now - _observedSince < SettleTime)
            return new(SupervisorAction.None, "codex detected • preparing backdrop");

        return new(SupervisorAction.Handoff, "switching this codex launch to backdrop mode");
    }

    public void MarkAttempt(CodexObservation observation, SupervisorAction action)
    {
        State.BlockedPackageIdentity = observation.PackageIdentity;
        State.LastAttemptedGeneration = observation.GenerationKey;
        State.LastFailure = "automatic retry stopped for this codex launch";
        State.FailureRecorded = false;
        if (action == SupervisorAction.Handoff) ProtectGeneration(observation.GenerationKey);
    }

    public void MarkSuccess(CodexObservation observation)
    {
        ProtectGeneration(observation.GenerationKey);
        State.BlockedPackageIdentity = null;
        State.LastAttemptedGeneration = null;
        State.LastFailure = null;
        State.FailureRecorded = false;
    }

    public void MarkFailure(string message)
    {
        State.LastFailure = message;
        State.FailureRecorded = true;
    }

    public void ClearForManualRetry()
    {
        State.BlockedPackageIdentity = null;
        State.LastAttemptedGeneration = null;
        State.LastFailure = null;
        State.FailureRecorded = false;
        State.RestartProtectedGenerations.Clear();
        _observedGeneration = null;
    }

    private void ProtectGeneration(string? generation)
    {
        if (string.IsNullOrWhiteSpace(generation) || State.RestartProtectedGenerations.Contains(generation)) return;
        State.RestartProtectedGenerations.Add(generation);
        const int maximumRememberedGenerations = 16;
        if (State.RestartProtectedGenerations.Count > maximumRememberedGenerations)
            State.RestartProtectedGenerations.RemoveRange(0, State.RestartProtectedGenerations.Count - maximumRememberedGenerations);
    }
}
