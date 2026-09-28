using CodexBackdrop;

var now = DateTimeOffset.Parse("2026-08-28T00:00:00Z");

static CodexObservation Normal(string generation, string package) => new(
    true,
    generation,
    package,
    42,
    false,
    false,
    DateTimeOffset.Parse("2026-08-28T00:00:01Z"),
    @"C:\Program Files\WindowsApps\OpenAI.Codex_test\app\ChatGPT.exe",
    "OpenAI.Codex_2p2nqsd0c76g0");
static CodexObservation Debuggable(string generation, string package, bool healthy = false) => new(
    true,
    generation,
    package,
    43,
    true,
    healthy,
    DateTimeOffset.Parse("2026-08-28T00:00:02Z"),
    @"C:\Program Files\WindowsApps\OpenAI.Codex_test\app\ChatGPT.exe",
    "OpenAI.Codex_2p2nqsd0c76g0");
static void Expect(SupervisorAction expected, SupervisorDecision actual, string name)
{
    if (actual.Action != expected) throw new Exception($"{name}: expected {expected}, got {actual.Action}");
}

static void ExpectShutdown(
    ShutdownContinuationDecision expected,
    ShutdownContinuationDecision actual,
    string name)
{
    if (actual != expected) throw new Exception($"{name}: expected {expected}, got {actual}");
}

var state = new SupervisorPersistentState();
var policy = new CodexSupervisorPolicy(state);
var busy = Debuggable("busy:1", "busy") with { InjectorRunning = true };
var waiting = policy.Decide(busy, true, now);
Expect(SupervisorAction.None, waiting, "an active injector repairs a stale health check itself");
if (waiting.Connected) throw new Exception("stale health must not report connected");
Expect(SupervisorAction.None, policy.Decide(busy with { DevToolsAvailable = false }, true, now.AddSeconds(5)),
    "a temporarily unavailable endpoint must not restart an active worker");
Expect(SupervisorAction.Attach, policy.Decide(busy, true, now, force: true), "explicit retry still replaces the worker");
var ambiguous = new CodexObservation(true, null, null, null, false, false, SafetyFailure: "multiple codex roots");
Expect(SupervisorAction.None, policy.Decide(ambiguous, true, now), "ambiguous identity fails closed");
var first = Normal("26.1:100", "26.1");
Expect(SupervisorAction.None, policy.Decide(first, true, now), "settle new launch");
Expect(SupervisorAction.Handoff, policy.Decide(first, true, now.AddSeconds(2)), "normal launch attaches automatically after settling");
Expect(SupervisorAction.Handoff, policy.Decide(first, true, now.AddSeconds(2), force: true), "explicit apply may hand off once");
policy.MarkAttempt(first, SupervisorAction.Handoff);
policy.MarkFailure("failed safely");
Expect(SupervisorAction.None, policy.Decide(first, true, now.AddSeconds(4)), "never repeat same launch");
Expect(SupervisorAction.None, policy.Decide(Normal("26.1:101", "26.1"), true, now.AddSeconds(5)), "breaker survives relaunch");

var updated = Normal("26.2:200", "26.2");
Expect(SupervisorAction.None, policy.Decide(updated, true, now.AddSeconds(6)), "settle update");
Expect(SupervisorAction.Handoff, policy.Decide(updated, true, now.AddSeconds(8)), "updated package gets automatic recovery despite old package failure");
Expect(SupervisorAction.Handoff, policy.Decide(updated, true, now.AddSeconds(8), force: true), "explicit apply authorizes the updated version");

policy.MarkAttempt(updated, SupervisorAction.Handoff);
var custom = Debuggable("26.2:201", "26.2");
Expect(SupervisorAction.Attach, policy.Decide(custom, true, now.AddSeconds(9)), "recover attach after interrupted handoff");
policy.MarkAttempt(custom, SupervisorAction.Attach);
Expect(SupervisorAction.None, policy.Decide(custom, true, now.AddSeconds(10)), "failed attach does not spin");

policy.MarkSuccess(custom);
Expect(SupervisorAction.None, policy.Decide(Debuggable("26.2:201", "26.2", healthy: true), true, now.AddSeconds(11)), "healthy layer stays connected");
Expect(SupervisorAction.None, policy.Decide(Normal("26.2:300", "26.2"), false, now.AddSeconds(12)), "pause prevents handoff");

var completedState = new SupervisorPersistentState();
var completedPolicy = new CodexSupervisorPolicy(completedState);
var completedGeneration = Normal("26.3:400", "26.3");
Expect(SupervisorAction.None, completedPolicy.Decide(completedGeneration, true, now), "settle completed handoff");
Expect(SupervisorAction.Handoff, completedPolicy.Decide(completedGeneration, true, now.AddSeconds(2)), "normal launch receives automatic handoff");
Expect(SupervisorAction.Handoff, completedPolicy.Decide(completedGeneration, true, now.AddSeconds(2), force: true), "explicitly authorize completed handoff");
completedPolicy.MarkAttempt(completedGeneration, SupervisorAction.Handoff);
completedPolicy.MarkSuccess(Debuggable("26.3:401", "26.3", healthy: true));
Expect(SupervisorAction.None, completedPolicy.Decide(completedGeneration, true, now.AddSeconds(4)), "completed handoff is never repeated for the same generation");
Expect(SupervisorAction.None, completedPolicy.Decide(Normal("26.3:401", "26.3"), true, now.AddSeconds(5)), "successful customized generation is never restarted automatically");

var failedState = new SupervisorPersistentState();
var failedPolicy = new CodexSupervisorPolicy(failedState);
var failedGeneration = Normal("26.4:500", "26.4");
Expect(SupervisorAction.None, failedPolicy.Decide(failedGeneration, true, now), "settle failed handoff");
Expect(SupervisorAction.Handoff, failedPolicy.Decide(failedGeneration, true, now.AddSeconds(2)), "first attempt is automatic");
Expect(SupervisorAction.Handoff, failedPolicy.Decide(failedGeneration, true, now.AddSeconds(2), force: true), "explicitly authorize failed handoff");
failedPolicy.MarkAttempt(failedGeneration, SupervisorAction.Handoff);
failedPolicy.MarkFailure("compatibility failed");
Expect(SupervisorAction.None, failedPolicy.Decide(Debuggable("26.4:501", "26.4"), true, now.AddSeconds(4)), "package breaker blocks automatic debug attach after a real failure");
failedPolicy.ClearForManualRetry();
Expect(SupervisorAction.Attach, failedPolicy.Decide(Debuggable("26.4:501", "26.4"), true, now.AddSeconds(5)), "manual retry clears the package breaker");

var restartedPolicy = new CodexSupervisorPolicy(failedState);
var anotherLaunch = Normal("26.4:600", "26.4");
Expect(SupervisorAction.None, restartedPolicy.Decide(anotherLaunch, true, now), "new launch settles");
Expect(SupervisorAction.Handoff, restartedPolicy.Decide(anotherLaunch, true, now.AddSeconds(2)), "later normal launch attaches too");
Expect(SupervisorAction.None, restartedPolicy.Decide(Normal("26.5:700", "26.5"), false, now.AddSeconds(3)), "kill switch also blocks a new package");
Expect(SupervisorAction.None, restartedPolicy.Decide(Debuggable("26.5:700", "26.5"), false, now.AddSeconds(4)), "kill switch blocks in-place attachment");

ExpectShutdown(
    ShutdownContinuationDecision.Refuse,
    CodexShutdownContinuationPolicy.Decide(rootIdentityStillExact: false, inventoryFullyVerifiable: true, freshSelectionAccepted: true),
    "changed root identity always refuses termination");
ExpectShutdown(
    ShutdownContinuationDecision.UseFreshSelection,
    CodexShutdownContinuationPolicy.Decide(rootIdentityStillExact: true, inventoryFullyVerifiable: true, freshSelectionAccepted: true),
    "fully verified live tree uses the fresh selection");
ExpectShutdown(
    ShutdownContinuationDecision.UseRecordedSelection,
    CodexShutdownContinuationPolicy.Decide(rootIdentityStillExact: true, inventoryFullyVerifiable: false, freshSelectionAccepted: false),
    "hidden exact root after graceful close uses only the pre-verified selection");

Console.WriteLine("supervisor policy: all bounded-recovery checks passed");
