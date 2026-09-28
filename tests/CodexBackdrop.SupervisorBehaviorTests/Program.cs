using System.Text.Json;
using CodexBackdrop;

var scratch = Path.Combine(Path.GetTempPath(), "supervisor-behavior-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
var failures = new List<string>();
try
{
    await Check("manual failure reaches caller", async (supervisor, runtime, paths) =>
    {
        runtime.Start = _ => throw new IOException("renderer unavailable");
        var error = await Failure(supervisor.RetryNowAsync());
        Expect(error is InvalidOperationException && error.Message.Contains("bd004")
            && error.InnerException is IOException, "attach failure did not carry its code and cause");
        Expect(runtime.Recoveries == 0 && supervisor.Enabled && runtime.Observation.IsRunning,
            "in-place failure must leave the existing codex session open");
        var state = JsonSerializer.Deserialize<SupervisorPersistentState>(File.ReadAllText(paths.SupervisorStatePath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Expect(state.FailureRecorded && state.BlockedPackageIdentity == "package-1", "failure was not durably blocked");
    });
    await Check("ambiguous process never reports success", async (supervisor, runtime, _) =>
    {
        runtime.Observation = new(true, null, null, null, false, false, SafetyFailure: "ambiguous process");
        var error = await Failure(supervisor.RetryNowAsync());
        Expect(error is InvalidOperationException && error.Message == "ambiguous process", "unsafe no-op reported success");
        Expect(runtime.Starts == 0, "ambiguous process was touched");
    });
    await Check("manual cancellation reaches active attach", async (supervisor, runtime, _) =>
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedToken = CancellationToken.None;
        runtime.Start = async token =>
        {
            receivedToken = token;
            entered.SetResult();
            await release.Task;
            token.ThrowIfCancellationRequested();
        };
        var operation = supervisor.RetryNowAsync(cancellationToken: cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        var reachedRuntime = receivedToken.IsCancellationRequested;
        release.SetResult();
        var error = await Failure(operation);
        Expect(reachedRuntime, "caller cancellation never reached runtime");
        Expect(error is OperationCanceledException, "cancelled attach reported success");
        Expect(runtime.Recoveries == 0, "cancellation launched a replacement");
    });
    await Check("health is checked after attach", async (supervisor, runtime, _) =>
    {
        runtime.ConfirmHealthy = false;
        Expect(await Failure(supervisor.RetryNowAsync()) is InvalidOperationException, "unhealthy result reported success");
        Expect(runtime.Recoveries == 0, "unhealthy in-place attachment must not replace codex");
    });
    await Check("normal recovery failure has a distinct error", async (supervisor, runtime, _) =>
    {
        runtime.Observation = runtime.Observation with { DevToolsAvailable = false };
        runtime.Start = _ => throw new IOException("renderer unavailable");
        runtime.Recover = _ => throw new IOException("process ownership changed");
        var error = await Failure(supervisor.RetryNowAsync());
        Expect(error?.Message.Contains("bd002") == true && runtime.Recoveries == 1,
            "failed normal recovery was hidden or repeated");
    });
    await Check("fresh launch failure restores normal codex and blocks retry", async (supervisor, runtime, paths) =>
    {
        runtime.Observation = runtime.Observation with { IsRunning = false };
        runtime.Start = _ => throw new IOException("renderer unavailable");
        var error = await Failure(supervisor.RetryNowAsync());
        Expect(error?.Message.Contains("bd001") == true && runtime.Recoveries == 1,
            "fresh launch failure did not restore normal codex");
        var state = JsonSerializer.Deserialize<SupervisorPersistentState>(File.ReadAllText(paths.SupervisorStatePath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Expect(state.FailureRecorded && state.BlockedPackageIdentity == "package-1" && supervisor.Enabled,
            "fresh launch failure lost the breaker or on preference");
    });
    await Check("fresh launch cancellation never reports success", async (supervisor, runtime, _) =>
    {
        runtime.Observation = runtime.Observation with { IsRunning = false };
        using var cancellation = new CancellationTokenSource();
        runtime.Start = _ => { cancellation.Cancel(); return Task.CompletedTask; };
        Expect(await Failure(supervisor.RetryNowAsync(cancellationToken: cancellation.Token)) is OperationCanceledException,
            "fresh launch ignored cancellation after runtime returned");
    });
    await Check("failed state save prevents runtime action", async (supervisor, runtime, paths) =>
    {
        Directory.CreateDirectory(paths.SupervisorStatePath);
        Expect(await Failure(supervisor.RetryNowAsync()) is IOException, "state write failure was swallowed");
        Expect(runtime.Starts == 0 && !supervisor.Enabled, "attachment continued without durable state");
    });
    await Check("successful attach persists recovery state", async (supervisor, runtime, paths) =>
    {
        await supervisor.RetryNowAsync();
        Expect(runtime.Starts == 1 && supervisor.Enabled, "attach did not run once");
        var state = JsonSerializer.Deserialize<SupervisorPersistentState>(File.ReadAllText(paths.SupervisorStatePath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Expect(!state.FailureRecorded && state.RestartProtectedGenerations.Contains("generation-1"), "success not saved");
    });
    await Check("kill switch cancels queued manual retries", async (supervisor, runtime, _) =>
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.Start = async token => { entered.TrySetResult(); await release.Task; token.ThrowIfCancellationRequested(); };
        var active = supervisor.RetryNowAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var queued = supervisor.RetryNowAsync();
        supervisor.SetEnabled(false);
        release.SetResult();
        Expect(await Failure(active) is OperationCanceledException, "active attachment survived the kill switch");
        var queuedError = await Failure(queued);
        Expect(queuedError is OperationCanceledException && runtime.Starts == 1 && !supervisor.Enabled,
            "queued retry re-enabled attachment after the kill switch");
        runtime.Start = _ => Task.CompletedTask;
        await supervisor.RetryNowAsync();
        Expect(supervisor.Enabled && runtime.Starts == 2, "a later explicit resume should still work");
    });
    await Check("open normally cancels attachment before waiting", async (supervisor, runtime, _) =>
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedToken = CancellationToken.None;
        runtime.Start = async token => { receivedToken = token; entered.SetResult(); await release.Task; token.ThrowIfCancellationRequested(); };
        var active = supervisor.RetryNowAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var recovery = supervisor.OpenNormallyAsync();
        var cancelledPromptly = receivedToken.IsCancellationRequested;
        release.SetResult();
        await Failure(active);
        await recovery.WaitAsync(TimeSpan.FromSeconds(2));
        Expect(cancelledPromptly && !supervisor.Enabled && runtime.NormalLaunches == 1,
            "normal recovery waited for attachment instead of stopping it first");
    });
    await Check("launch and package update both attach automatically", async (supervisor, runtime, _) =>
    {
        runtime.Observation = runtime.Observation with { DevToolsAvailable = false };
        supervisor.Start();
        await Until(() => runtime.Restarts == 1 && runtime.Observation.InjectorHealthy);
        runtime.Observation = runtime.Observation with
        {
            GenerationKey = "generation-2", PackageIdentity = "package-2",
            DevToolsAvailable = false, InjectorHealthy = false,
        };
        await Until(() => runtime.Restarts == 2 && runtime.Observation.InjectorHealthy);
        await Task.Delay(1200);
        Expect(runtime.Starts == 2, "a healthy session was restarted again");
        supervisor.SetEnabled(false);
        runtime.Observation = runtime.Observation with
        {
            GenerationKey = "generation-3", PackageIdentity = "package-3",
            DevToolsAvailable = false, InjectorHealthy = false,
        };
        await Task.Delay(2200);
        Expect(runtime.Starts == 2, "a later update overrode the kill switch");
    });
    await Check("automatic failure is attempted once", async (supervisor, runtime, _) =>
    {
        runtime.Start = _ => throw new IOException("incompatible renderer");
        supervisor.Start();
        await Task.Delay(2200);
        Expect(runtime.Starts == 1, "background failure retried");
        Expect(runtime.Recoveries == 0, "automatic in-place failure closed the existing session");
    });
}
finally
{
    Directory.Delete(scratch, recursive: true);
}
if (failures.Count > 0) throw new Exception(string.Join(Environment.NewLine, failures));
Console.WriteLine("supervisor behavior: all 13 checks passed");

static async Task Until(Func<bool> ready)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
    while (!ready()) await Task.Delay(50, timeout.Token);
}

async Task Check(string name, Func<CodexSupervisor, RuntimeManager, AppPaths, Task> action)
{
    var root = Path.Combine(scratch, Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var paths = new AppPaths(root, Path.Combine(root, "config.json"), Path.Combine(root, "runtime.log"),
        Path.Combine(root, "supervisor-state.json"), root);
    var runtime = new RuntimeManager();
    await using var supervisor = new CodexSupervisor(runtime, paths, enabled: true);
    try { await action(supervisor, runtime, paths); }
    catch (Exception error) { failures.Add(name + ": " + error.Message); }
}

static async Task<Exception?> Failure(Task task)
{
    try { await task.WaitAsync(TimeSpan.FromSeconds(4)); return null; }
    catch (Exception error) { return error; }
}

static void Expect(bool value, string message)
{
    if (!value) throw new Exception(message);
}

namespace CodexBackdrop
{
    // This harness links the production supervisor and replaces only its OS boundary.
    public sealed class RuntimeManager
    {
        public CodexObservation Observation { get; set; } = new(true, "generation-1", "package-1", 42, true, false,
            DateTimeOffset.UtcNow, @"C:\test\ChatGPT.exe", "OpenAI.Codex_2p2nqsd0c76g0");
        public Func<CancellationToken, Task> Start { get; set; } = _ => Task.CompletedTask;
        public bool ConfirmHealthy { get; set; } = true;
        public int Starts;
        public int Restarts;
        public int NormalLaunches;
        public int Recoveries;
        public Func<CancellationToken, Task> Recover { get; set; } = _ => Task.CompletedTask;
        public async Task RecoverNormalAfterFailureAsync(CodexObservation attempted, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Recoveries++;
            await Recover(token);
            Observation = Observation with { IsRunning = true, DevToolsAvailable = false, InjectorHealthy = false };
        }
        public CodexObservation ObserveCodex() => Observation;
        public async Task StartAsync(bool restartCodex, CodexObservation observation, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Starts);
            if (restartCodex) Interlocked.Increment(ref Restarts);
            await Start(cancellationToken);
            Observation = Observation with { IsRunning = true, DevToolsAvailable = true, InjectorHealthy = ConfirmHealthy };
        }
        public Task LaunchCustomizedAsync(CancellationToken token) => StartAsync(false, Observation, token);
        public Task LaunchNormalAsync(CancellationToken token) { NormalLaunches++; return Task.CompletedTask; }
        public Task StartInspectionAsync(bool restartCodex, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopInjectorAsync() => Task.CompletedTask;
        public Task WriteLogAsync(string message) => Task.CompletedTask;
    }
}
