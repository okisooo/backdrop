using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;

namespace CodexBackdrop;

public sealed class RuntimeManager(AppPaths paths) : IAsyncDisposable
{
    private static readonly TimeSpan GracefulShutdownTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ForcedShutdownTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LaunchIdentityTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RollbackTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(12);
    // Codex 26.924 reads its startup policy (network) before it opens a window,
    // then renders the shell; a slow start must not be mistaken for a failure
    // that restarts codex a second time.
    private static readonly TimeSpan DevToolsReadyTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FirstAttachTimeout = TimeSpan.FromSeconds(45);

    private readonly BackdropInjector _injector = new(paths);
    private readonly HttpClient _probeHttp = new() { Timeout = TimeSpan.FromMilliseconds(650) };
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _generationLock = new();
    private CodexLaunchGeneration? _preferredGeneration;
    private CodexLaunchGeneration? _normalGeneration;
    private int _disposed;

    private sealed class LaunchAttempt
    {
        public bool ProcessStarted { get; set; }
        public CodexProcessIdentity? StartedIdentity { get; set; }
        public CodexProcessLease? StartedLease { get; set; }
        public CodexLaunchGeneration? RecordedGeneration { get; set; }
        public bool CleanupConfirmed { get; set; }
    }

    public bool CustomizationDesired { get; private set; }
    public bool InjectorHealthy => _injector.IsHealthy;

    public CodexObservation ObserveCodex()
    {
        EnterLifecycleGate();
        try { return ObserveCodexCore(); }
        finally { _lifecycleGate.Release(); }
    }

    private CodexObservation ObserveCodexCore()
    {
        var settings = BackdropSettings.Load(paths.ConfigPath);
        var inventory = WindowsCodexDiscovery.Capture();
        if (inventory.HasUnverifiableCodexCandidate)
            return UnsafeObservation("an unverifiable codex process is running • no automatic action");
        if (inventory.Processes.Count == 0)
            return new(false, null, null, null, false, false);

        var generation = SelectGeneration(inventory, settings.DevToolsPort, out var failure);
        if (generation is null) return UnsafeObservation(failure);

        SetPreferredGeneration(generation);
        var devToolsAvailable = IsDevToolsAvailable(settings.DevToolsPort, generation);
        var root = generation.Root;
        return new CodexObservation(
            true,
            GenerationKey(generation),
            generation.Package.PackageIdentity,
            root.ProcessId,
            devToolsAvailable,
            devToolsAvailable && _injector.IsHealthy,
            root.StartTimeUtc,
            generation.Package.ExecutablePath,
            generation.Package.PublisherIdentity,
            InjectorRunning: _injector.IsRunning);
    }

    public bool IsDevToolsAvailable(int port = 9223)
    {
        EnterLifecycleGate();
        try { return IsDevToolsAvailableCore(port); }
        finally { _lifecycleGate.Release(); }
    }

    private bool IsDevToolsAvailableCore(int port = 9223)
    {
        var inventory = WindowsCodexDiscovery.Capture();
        if (inventory.HasUnverifiableCodexCandidate || inventory.Processes.Count == 0) return false;
        var generation = SelectGeneration(inventory, port, out _);
        return generation is not null && IsDevToolsAvailable(port, generation);
    }

    public Task StartAsync(
        bool restartCodex,
        CodexObservation? observation = null,
        CancellationToken cancellationToken = default) =>
        RunSerializedAsync(
            () => StartCoreAsync(restartCodex, observation, cancellationToken),
            cancellationToken);

    private async Task StartCoreAsync(
        bool restartCodex,
        CodexObservation? observation = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CustomizationDesired = true;
        CodexLaunchGeneration? launchedGeneration = null;
        CodexLaunchGeneration? sourceGeneration = null;
        string? executable = null;
        var sourceStopAttempted = false;
        var launchAttempt = new LaunchAttempt();

        try
        {
            var settings = BackdropSettings.Load(paths.ConfigPath);
            await StopInjectorCoreAsync().ConfigureAwait(false);

            if (restartCodex)
            {
                sourceGeneration = RequireGeneration(observation ?? ObserveCodexCore());
                executable = await ResolveCodexExecutableAsync(cancellationToken).ConfigureAwait(false);
                var rendererProfile = ResolveRendererProfile(settings);
                Directory.CreateDirectory(rendererProfile);
                EnsurePortCanBeClaimed(settings.DevToolsPort, sourceGeneration);

                sourceStopAttempted = true;
                await StopCodexAsync(sourceGeneration, cancellationToken).ConfigureAwait(false);
                if (IsPortListening(settings.DevToolsPort))
                    throw new InvalidOperationException("the local backdrop port did not close with the recorded codex generation");

                launchedGeneration = await LaunchCodexAsync(
                    executable,
                    customized: true,
                    settings.DevToolsPort,
                    rendererProfile,
                    cancellationToken,
                    launchAttempt).ConfigureAwait(false);
                SetPreferredGeneration(launchedGeneration);
            }
            else
            {
                launchedGeneration = RequireGeneration(observation ?? ObserveCodexCore());
                SetPreferredGeneration(launchedGeneration);
                if (!IsDevToolsAvailable(settings.DevToolsPort, launchedGeneration))
                    throw new InvalidOperationException("codex needs one controlled restart to enable the backdrop");
            }

            var controlledGeneration = launchedGeneration ?? throw new InvalidOperationException("codex launch generation was not recorded");
            await WaitForDevToolsAsync(
                settings.DevToolsPort,
                controlledGeneration,
                DevToolsReadyTimeout,
                cancellationToken).ConfigureAwait(false);
            await _injector.StartAsync(
                settings.DevToolsPort,
                FirstAttachTimeout,
                token => ValidateGenerationEndpointAsync(controlledGeneration, settings.DevToolsPort, token, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await WriteLogAsync($"backdrop runtime started for {GenerationKey(controlledGeneration)}").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            CustomizationDesired = false;
            try { await StopInjectorCoreAsync().ConfigureAwait(false); } catch { }
            if (!launchAttempt.CleanupConfirmed)
                launchAttempt.CleanupConfirmed = await TryCleanupLaunchAttemptAsync(launchAttempt).ConfigureAwait(false);
            await WriteLogAsync("backdrop handoff canceled safely; no replacement codex launch was requested").ConfigureAwait(false);
            throw;
        }
        catch
        {
            CustomizationDesired = false;
            try { await StopInjectorCoreAsync().ConfigureAwait(false); } catch { }
            if (restartCodex && sourceStopAttempted && executable is not null && sourceGeneration is not null)
                await TryRollbackToNormalAsync(executable, sourceGeneration, launchedGeneration, launchAttempt, cancellationToken).ConfigureAwait(false);
            await WriteLogAsync("backdrop start failed; automatic restart disabled until the user applies again").ConfigureAwait(false);
            throw;
        }
    }

    public Task LaunchCustomizedAsync(CancellationToken cancellationToken = default) =>
        RunSerializedAsync(() => LaunchCustomizedCoreAsync(cancellationToken), cancellationToken);

    private async Task LaunchCustomizedCoreAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var inventory = WindowsCodexDiscovery.Capture();
        if (inventory.HasUnverifiableCodexCandidate || inventory.Processes.Count > 0)
            throw new InvalidOperationException("codex is already open or starting • no second instance was launched");

        CustomizationDesired = true;
        CodexLaunchGeneration? launchedGeneration = null;
        var launchAttempt = new LaunchAttempt();
        try
        {
            var settings = BackdropSettings.Load(paths.ConfigPath);
            await StopInjectorCoreAsync().ConfigureAwait(false);
            if (IsPortListening(settings.DevToolsPort))
                throw new InvalidOperationException($"local port {settings.DevToolsPort} is already in use • codex was not launched");

            var rendererProfile = ResolveRendererProfile(settings);
            Directory.CreateDirectory(rendererProfile);
            var executable = await ResolveCodexExecutableAsync(cancellationToken).ConfigureAwait(false);
            launchedGeneration = await LaunchCodexAsync(
                executable,
                customized: true,
                settings.DevToolsPort,
                rendererProfile,
                cancellationToken,
                launchAttempt).ConfigureAwait(false);
            SetPreferredGeneration(launchedGeneration);
            var controlledGeneration = launchedGeneration ?? throw new InvalidOperationException("codex launch generation was not recorded");
            await WaitForDevToolsAsync(settings.DevToolsPort, controlledGeneration, DevToolsReadyTimeout, cancellationToken).ConfigureAwait(false);
            await _injector.StartAsync(
                settings.DevToolsPort,
                FirstAttachTimeout,
                token => ValidateGenerationEndpointAsync(controlledGeneration, settings.DevToolsPort, token, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await WriteLogAsync($"codex launched once with backdrop as {GenerationKey(controlledGeneration)}").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            CustomizationDesired = false;
            try { await StopInjectorCoreAsync().ConfigureAwait(false); } catch { }
            if (!launchAttempt.CleanupConfirmed)
                launchAttempt.CleanupConfirmed = await TryCleanupLaunchAttemptAsync(launchAttempt).ConfigureAwait(false);
            await WriteLogAsync("customized codex launch canceled safely; no replacement launch was requested").ConfigureAwait(false);
            throw;
        }
        catch
        {
            CustomizationDesired = false;
            try { await StopInjectorCoreAsync().ConfigureAwait(false); } catch { }
            if (!launchAttempt.CleanupConfirmed)
                launchAttempt.CleanupConfirmed = await TryCleanupLaunchAttemptAsync(launchAttempt).ConfigureAwait(false);
            throw;
        }
    }

    public Task LaunchNormalAsync(CancellationToken cancellationToken = default) =>
        RunSerializedAsync(() => LaunchNormalCoreAsync(cancellationToken), cancellationToken);

    public Task RecoverNormalAfterFailureAsync(CodexObservation attempted, CancellationToken cancellationToken) =>
        RunSerializedAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            CustomizationDesired = false;
            await StopInjectorCoreAsync().ConfigureAwait(false);
            var current = ObserveCodexCore();
            CodexLaunchGeneration? source = null;
            if (current.IsRunning)
            {
                source = RequireGeneration(current);
                if (source == _normalGeneration) return; // The handoff already rolled back.
                var preferred = GetPreferredGeneration();
                var recorded = preferred is not null && GenerationKey(preferred) == current.GenerationKey;
                if (current.GenerationKey != attempted.GenerationKey &&
                    (!recorded || (attempted.PackageIdentity is not null && current.PackageIdentity != attempted.PackageIdentity)))
                    throw new InvalidOperationException("codex changed during recovery; the new session was left untouched");
                if (current.GenerationKey == attempted.GenerationKey && !attempted.DevToolsAvailable && !recorded)
                    return; // The original normal session was never replaced.
            }
            var executable = await ResolveCodexExecutableAsync(cancellationToken).ConfigureAwait(false);
            if (source is not null) await StopCodexAsync(source, cancellationToken).ConfigureAwait(false);
            await LaunchNormalReplacementAsync(executable, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    private async Task LaunchNormalCoreAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CustomizationDesired = false;
        await StopInjectorCoreAsync().ConfigureAwait(false);
        var executable = await ResolveCodexExecutableAsync(cancellationToken).ConfigureAwait(false);
        var observation = ObserveCodexCore();
        if (observation.IsRunning)
        {
            var source = RequireGeneration(observation);
            await StopCodexAsync(source, cancellationToken).ConfigureAwait(false);
        }

        await LaunchNormalReplacementAsync(executable, cancellationToken).ConfigureAwait(false);
    }

    private async Task LaunchNormalReplacementAsync(string executable, CancellationToken cancellationToken)
    {
        var launchAttempt = new LaunchAttempt();
        var launched = await LaunchCodexAsync(executable, customized: false, 0, null, cancellationToken, launchAttempt).ConfigureAwait(false);
        SetPreferredGeneration(launched);
        _normalGeneration = launched;
        await WriteLogAsync($"codex launched normally as {GenerationKey(launched)}; backdrop disabled").ConfigureAwait(false);
    }

    public void StopInjector()
    {
        EnterLifecycleGate();
        try { _injector.Stop(); }
        finally { _lifecycleGate.Release(); }
    }

    public Task StopInjectorAsync(CancellationToken cancellationToken = default) =>
        RunSerializedAsync(StopInjectorCoreAsync, cancellationToken);

    private Task StopInjectorCoreAsync() => _injector.StopAsync();

    /// <summary>Applies a settings change now instead of on the next poll.</summary>
    public void NudgeInjector() => _injector.Nudge();

    /// <summary>
    /// Stops the injector and takes the backdrop out of the exact Codex
    /// generation that owns the local endpoint. Codex keeps running.
    /// </summary>
    public Task RemoveBackdropAsync(CancellationToken cancellationToken = default) =>
        RunSerializedAsync(async () =>
        {
            await StopInjectorCoreAsync().ConfigureAwait(false);
            var port = BackdropSettings.Load(paths.ConfigPath).DevToolsPort;
            if (!IsDevToolsAvailableCore(port)) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await _injector.RemoveAsync(port, timeout.Token).ConfigureAwait(false);
            await WriteLogAsync("backdrop removed; codex left running").ConfigureAwait(false);
        }, cancellationToken);

    public Task StopCodexAsync(CancellationToken cancellationToken = default) =>
        RunSerializedAsync(() => StopCodexFromCurrentCoreAsync(cancellationToken), cancellationToken);

    private async Task StopCodexFromCurrentCoreAsync(CancellationToken cancellationToken)
    {
        var generation = RequireGeneration(ObserveCodexCore());
        await StopCodexAsync(generation, cancellationToken).ConfigureAwait(false);
    }

    private async Task StopCodexAsync(CodexLaunchGeneration generation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var inventory = WindowsCodexDiscovery.Capture();
        if (inventory.HasUnverifiableCodexCandidate)
            throw new InvalidOperationException("codex shutdown refused because process ownership could not be fully verified");
        var selection = CodexProcessOwnership.SelectOwnedTargets(generation, inventory.Processes);
        if (!selection.Accepted)
        {
            if (IsGenerationConfirmedAbsent(inventory, generation))
            {
                await WriteLogAsync($"recorded codex generation already exited: {GenerationKey(generation)}").ConfigureAwait(false);
                ClearPreferredGeneration(generation);
                return;
            }

            throw new InvalidOperationException($"codex shutdown refused: {DescribeOwnershipFailure(selection.Failure)}");
        }
        var initialSelection = selection;

        await WriteLogAsync(
            $"codex shutdown requested for exact generation {GenerationKey(generation)}; {selection.Targets.Count} verified packaged process(es) in tree").ConfigureAwait(false);

        if (!WindowsCodexDiscovery.TryOpenExact(generation.Root, generation.Package, out var rootLease) || rootLease is null)
        {
            inventory = WindowsCodexDiscovery.Capture();
            if (!inventory.HasUnverifiableCodexCandidate && IsGenerationConfirmedAbsent(inventory, generation))
            {
                ClearPreferredGeneration(generation);
                return;
            }
            throw new InvalidOperationException("codex shutdown refused because the recorded root could not be revalidated");
        }

        using (rootLease)
        {
            try
            {
                if (!rootLease.CloseMainWindow())
                    await WriteLogAsync($"codex close request found no visible main window for pid {rootLease.Id}").ConfigureAwait(false);
            }
            catch (Exception error) { await WriteLogAsync($"codex close failed for pid {rootLease.Id}: {error.Message}").ConfigureAwait(false); }
        }

        if (await WaitForGenerationExitAsync(generation, GracefulShutdownTimeout, cancellationToken).ConfigureAwait(false))
        {
            await WriteLogAsync($"codex generation exited gracefully: {GenerationKey(generation)}").ConfigureAwait(false);
            ClearPreferredGeneration(generation);
            return;
        }

        inventory = WindowsCodexDiscovery.Capture();
        selection = CodexProcessOwnership.SelectOwnedTargets(generation, inventory.Processes);
        var rootIdentityStillExact = WindowsCodexDiscovery.TryOpenExact(
            generation.Root,
            generation.Package,
            out var forcedRootLease) && forcedRootLease is not null;
        var continuation = CodexShutdownContinuationPolicy.Decide(
            rootIdentityStillExact,
            !inventory.HasUnverifiableCodexCandidate,
            selection.Accepted);
        if (continuation == ShutdownContinuationDecision.Refuse)
        {
            await WriteLogAsync($"codex termination refused after identity changed: {GenerationKey(generation)}").ConfigureAwait(false);
            throw new InvalidOperationException("codex identity changed during shutdown • no process was terminated");
        }

        var exactRootLease = forcedRootLease!;
        using (exactRootLease)
        {
            await WriteLogAsync(
                continuation == ShutdownContinuationDecision.UseFreshSelection
                    ? $"graceful close timed out; terminating fresh exact targets for root pid {exactRootLease.Id}"
                    : $"graceful close hid the exact root window; terminating only pre-verified targets for root pid {exactRootLease.Id}").ConfigureAwait(false);
        }

        var forcedSelection = continuation == ShutdownContinuationDecision.UseFreshSelection
            ? selection
            : initialSelection;
        await KillSelectedTargetsAsync(generation, forcedSelection, cancellationToken).ConfigureAwait(false);

        if (!await WaitForGenerationExitAsync(generation, ForcedShutdownTimeout, cancellationToken).ConfigureAwait(false))
        {
            await WriteLogAsync($"codex shutdown failed safely; exact generation remains: {GenerationKey(generation)}").ConfigureAwait(false);
            throw new InvalidOperationException("the recorded codex generation did not close • no replacement was launched");
        }

        await WriteLogAsync($"exact codex generation exited after bounded termination: {GenerationKey(generation)}").ConfigureAwait(false);
        ClearPreferredGeneration(generation);
    }

    private async Task KillSelectedTargetsAsync(
        CodexLaunchGeneration generation,
        CodexTargetSelection recordedSelection,
        CancellationToken cancellationToken)
    {
        var knownTargets = recordedSelection.Targets
            .GroupBy(target => target.Identity)
            .ToDictionary(group => group.Key, group => group.First());
        var deadline = DateTimeOffset.UtcNow + ForcedShutdownTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!WindowsCodexDiscovery.IsGenerationRunning(generation)) return;

            var inventory = WindowsCodexDiscovery.Capture();
            var selection = CodexProcessOwnership.SelectOwnedTargets(generation, inventory.Processes);
            var continuation = CodexShutdownContinuationPolicy.Decide(
                rootIdentityStillExact: true,
                inventoryFullyVerifiable: !inventory.HasUnverifiableCodexCandidate,
                freshSelectionAccepted: selection.Accepted);
            IReadOnlyList<CodexProcessSnapshot> targets;
            if (continuation == ShutdownContinuationDecision.UseFreshSelection)
            {
                foreach (var target in selection.Targets)
                    knownTargets[target.Identity] = target;
                targets = selection.Targets
                    .OrderBy(target => target.Identity == generation.Root ? 1 : 0)
                    .ToArray();
            }
            else
            {
                targets = knownTargets.Values
                    .OrderBy(target => target.Identity == generation.Root ? 1 : 0)
                    .ThenBy(target => target.Identity.ProcessId)
                    .ToArray();
                if (targets.Count == 0)
                    throw new InvalidOperationException(
                        $"codex termination refused after identity changed: {DescribeOwnershipFailure(selection.Failure)}");
            }

            var terminated = false;
            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!WindowsCodexDiscovery.TryOpenExact(target.Identity, generation.Package, out var processLease) || processLease is null)
                    continue;

                using (processLease)
                {
                    try
                    {
                        if (processLease.HasExited) continue;
                        processLease.Kill();
                        terminated = true;
                    }
                    catch (InvalidOperationException)
                    {
                        // The exact process exited between revalidation and Kill.
                    }
                    catch (Exception error)
                    {
                        throw new InvalidOperationException(
                            $"exact codex process {target.Identity.ProcessId} could not be terminated safely",
                            error);
                    }
                }
            }

            if (!terminated)
            {
                if (!WindowsCodexDiscovery.IsGenerationRunning(generation)) return;
                throw new InvalidOperationException("codex termination could not revalidate an exact selected process");
            }

            await Task.Delay(75, cancellationToken).ConfigureAwait(false);
        }

        if (!await WaitForGenerationExitAsync(generation, TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("codex termination timed out while verified descendants remained");
    }

    private CodexLaunchGeneration? SelectGeneration(
        CodexProcessInventory inventory,
        int port,
        out string failure)
    {
        if (inventory.HasUnverifiableCodexCandidate)
        {
            failure = "codex process ownership could not be fully verified • no automatic action";
            return null;
        }

        var preferred = GetPreferredGeneration();
        if (preferred is not null)
        {
            var owned = CodexProcessOwnership.SelectOwnedTargets(preferred, inventory.Processes);
            if (owned.Accepted)
            {
                failure = "";
                return preferred;
            }
            if (!IsExactGenerationRunning(preferred)) ClearPreferredGeneration(preferred);
        }

        var visibleRoots = inventory.Processes
            .Where(process => process.Role == CodexProcessRole.Root && process.HasVisibleWindow)
            .ToArray();
        if (visibleRoots.Length == 1)
        {
            var package = PackageContract(visibleRoots[0]);
            var selected = CodexProcessOwnership.SelectGeneration(inventory.Processes, package);
            if (selected.Accepted)
            {
                failure = "";
                return selected.Generation;
            }

            failure = DescribeOwnershipFailure(selected.Failure);
            return null;
        }

        failure = visibleRoots.Length > 1
            ? "multiple codex windows are running • no automatic action"
            : "codex is running without one verifiable main window • no automatic action";
        return null;
    }

    private static bool IsExactGenerationRunning(CodexLaunchGeneration generation)
    {
        if (!WindowsCodexDiscovery.TryOpenExact(generation.Root, generation.Package, out var lease) ||
            lease is null) return false;

        using (lease)
        {
            return !lease.HasExited;
        }
    }

    private bool IsDevToolsAvailable(int port, CodexLaunchGeneration generation)
    {
        if (!WindowsCodexDiscovery.TryGetExclusiveLoopbackListenerOwner(port, out var listenerProcessId) ||
            listenerProcessId != generation.Root.ProcessId ||
            !WindowsCodexDiscovery.TryOpenExact(generation.Root, generation.Package, out var rootLease) ||
            rootLease is null) return false;

        using (rootLease)
        {
            try
            {
                if (rootLease.HasExited) return false;
                var json = _probeHttp.GetStringAsync($"http://127.0.0.1:{port}/json/list").GetAwaiter().GetResult();
                using var document = JsonDocument.Parse(json);
                return document.RootElement.ValueKind == JsonValueKind.Array && document.RootElement.EnumerateArray().Any(target =>
                    target.TryGetProperty("type", out var type) && type.GetString() == "page" &&
                    target.TryGetProperty("url", out var url) && url.GetString() == "app://-/index.html" &&
                    target.TryGetProperty("webSocketDebuggerUrl", out var socket) && IsExpectedSocket(socket.GetString(), port));
            }
            catch
            {
                return false;
            }
        }
    }

    private static async Task EnsureNoCodexProcessForLaunchAsync(CancellationToken cancellationToken)
    {
        var cleanSamples = 0;
        while (cleanSamples < 2)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inventory = WindowsCodexDiscovery.Capture();
            if (inventory.HasUnverifiableCodexCandidate || inventory.Processes.Count > 0)
                throw new InvalidOperationException("codex is already open or starting • no second instance was launched");
            cleanSamples++;
            if (cleanSamples < 2)
                await Task.Delay(75, cancellationToken).ConfigureAwait(false);
        }
    }

    private static ValueTask<bool> ValidateGenerationEndpointAsync(
        CodexLaunchGeneration generation,
        int port,
        CancellationToken cancellationToken,
        CancellationToken externalCancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested || externalCancellationToken.IsCancellationRequested)
            return new(false);
        if (!WindowsCodexDiscovery.TryGetExclusiveLoopbackListenerOwner(port, out var owner) ||
            owner != generation.Root.ProcessId ||
            !WindowsCodexDiscovery.TryOpenExact(generation.Root, generation.Package, out var processLease) ||
            processLease is null) return new(false);
        using (processLease)
        {
            return new ValueTask<bool>(!processLease.HasExited);
        }
    }

    private async Task<bool> TryCleanupGenerationAsync(CodexLaunchGeneration generation)
    {
        using var cleanupTimeout = new CancellationTokenSource(CleanupTimeout);
        try
        {
            var initialInventory = WindowsCodexDiscovery.Capture();
            if (initialInventory.HasUnverifiableCodexCandidate) return false;
            if (!IsGenerationConfirmedAbsent(initialInventory, generation))
                await StopCodexAsync(generation, cleanupTimeout.Token).ConfigureAwait(false);

            if (!await WaitForGenerationExitAsync(
                    generation,
                    TimeSpan.FromSeconds(1),
                    cleanupTimeout.Token).ConfigureAwait(false)) return false;

            for (var sample = 0; sample < 2; sample++)
            {
                var inventory = WindowsCodexDiscovery.Capture();
                if (!IsGenerationConfirmedAbsent(inventory, generation))
                    return false;
                if (sample != 1)
                    await Task.Delay(75, cleanupTimeout.Token).ConfigureAwait(false);
            }

            ClearPreferredGeneration(generation);
            return true;
        }
        catch (OperationCanceledException) when (cleanupTimeout.IsCancellationRequested)
        {
            await WriteLogAsync($"exact codex cleanup timed out: {GenerationKey(generation)}").ConfigureAwait(false);
            return false;
        }
        catch (Exception error)
        {
            await WriteLogAsync($"exact codex cleanup failed safely: {error.Message}").ConfigureAwait(false);
            return false;
        }
    }

    private async Task<bool> TryCleanupLaunchAttemptAsync(LaunchAttempt attempt)
    {
        if (!attempt.ProcessStarted) return true;

        if (attempt.RecordedGeneration is not null &&
            await TryCleanupGenerationAsync(attempt.RecordedGeneration).ConfigureAwait(false))
            return true;

        var lease = attempt.StartedLease;
        if (lease is null) return false;
        try
        {
            if (!lease.HasExited)
            {
                lease.Kill();
                if (!lease.WaitForExit(TimeSpan.FromSeconds(2))) return false;
            }

            return attempt.RecordedGeneration is null ||
                await WaitForGenerationExitAsync(
                    attempt.RecordedGeneration,
                    TimeSpan.FromSeconds(1),
                    CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    private async Task<CodexLaunchGeneration> LaunchCodexAsync(
        string executable,
        bool customized,
        int port,
        string? userDataDirectory,
        CancellationToken cancellationToken,
        LaunchAttempt attempt)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureNoCodexProcessForLaunchAsync(cancellationToken).ConfigureAwait(false);
        if (!WindowsCodexDiscovery.IsExpectedPackageExecutable(executable))
            throw new InvalidOperationException("the official codex executable could not be verified");
        if (customized && (port is < 1 or > 65535 || IsPortListening(port)))
            throw new InvalidOperationException($"local port {port} is already in use • codex was not launched");

        var expectedPackage = PackageContract(executable);

        var arguments = new List<string>();
        if (customized)
        {
            arguments.Add("--remote-debugging-address=127.0.0.1");
            arguments.Add($"--remote-debugging-port={port}");
            arguments.Add($"--remote-allow-origins=http://127.0.0.1:{port}");
        }
        if (!string.IsNullOrWhiteSpace(userDataDirectory))
            arguments.Add($"--user-data-dir={Path.GetFullPath(userDataDirectory)}");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requestedUtc = DateTimeOffset.UtcNow;
            var launchedProcessId = CodexPackageActivation.Activate(executable, arguments);
            attempt.ProcessStarted = true;
            // Capture the identity before cancellation, package correlation, or
            // any asynchronous wait can let a short-lived process disappear.
            var directIdentity = TryCaptureActivatedIdentity(launchedProcessId, requestedUtc);
            attempt.StartedIdentity = directIdentity;
            var deadline = DateTimeOffset.UtcNow + LaunchIdentityTimeout;
            while (directIdentity is null && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
                directIdentity = TryCaptureActivatedIdentity(launchedProcessId, requestedUtc);
                attempt.StartedIdentity = directIdentity;
            }

            if (directIdentity is null)
                throw new InvalidOperationException("codex launch identity could not be captured safely");

            // Record the activated identity before any package or window query.
            // Every failure after this point can therefore clean up only this exact
            // launch generation, never a process selected by timing or path alone.
            var directGeneration = new CodexLaunchGeneration(directIdentity, expectedPackage);
            attempt.RecordedGeneration = directGeneration;

            // Keep one exact handle available while correlation is pending.  A
            // package/window query may fail even though activation already
            // gave us a valid, safe identity to clean up.
            TryOpenStartedLease(attempt, directGeneration);

            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var inventory = WindowsCodexDiscovery.Capture();
                if (attempt.StartedLease is null)
                    TryOpenStartedLease(attempt, directGeneration);

                // A just-launched package can be temporarily absent from the
                // process inventory while its window/token metadata settles.
                // Do not broaden control to that unsafe inventory; keep
                // correlating only the already recorded identity and accept it
                // only after a complete fresh capture becomes available.
                if (inventory.HasUnverifiableCodexCandidate)
                {
                    await Task.Delay(150, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var direct = inventory.Processes.FirstOrDefault(process =>
                    process.Identity == directGeneration.Root &&
                    process.Role == CodexProcessRole.Root);
                if (direct is not null)
                {
                    var selected = CodexProcessOwnership.SelectOwnedTargets(directGeneration, inventory.Processes);
                    // The launch may not be accepted while another official
                    // ChatGPT generation is present in the same fresh capture;
                    // ownership selection intentionally ignores unrelated
                    // roots, so require every captured process to be ours.
                    if (selected.Accepted && selected.Targets.Count == inventory.Processes.Count)
                    {
                        await WriteLogAsync($"codex launch recorded as {GenerationKey(directGeneration)} ({(customized ? "backdrop" : "normal")} mode)").ConfigureAwait(false);
                        attempt.CleanupConfirmed = false;
                        return directGeneration;
                    }
                }

                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            }

            throw new InvalidOperationException("codex launch could not be tied to the activated process identity");
        }
        catch
        {
            if (attempt.ProcessStarted)
            {
                attempt.CleanupConfirmed = await TryCleanupLaunchAttemptAsync(attempt).ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            attempt.StartedLease?.Dispose();
            attempt.StartedLease = null;
        }
    }

    private static void TryOpenStartedLease(LaunchAttempt attempt, CodexLaunchGeneration generation)
    {
        if (attempt.StartedLease is not null ||
            !WindowsCodexDiscovery.TryOpenExact(generation.Root, generation.Package, out var lease) ||
            lease is null) return;

        attempt.StartedLease = lease;
    }

    private async Task TryRollbackToNormalAsync(
        string executable,
        CodexLaunchGeneration sourceGeneration,
        CodexLaunchGeneration? launchedGeneration,
        LaunchAttempt launchAttempt,
        CancellationToken operationCancellationToken)
    {
        if (operationCancellationToken.IsCancellationRequested)
        {
            await WriteLogAsync("normal rollback skipped because the handoff was canceled").ConfigureAwait(false);
            return;
        }

        using var rollbackTimeout = CancellationTokenSource.CreateLinkedTokenSource(operationCancellationToken);
        rollbackTimeout.CancelAfter(RollbackTimeout);
        try
        {
            rollbackTimeout.Token.ThrowIfCancellationRequested();
            launchedGeneration ??= launchAttempt.RecordedGeneration;
            if (launchAttempt.ProcessStarted)
            {
                if (!launchAttempt.CleanupConfirmed)
                    launchAttempt.CleanupConfirmed = await TryCleanupLaunchAttemptAsync(launchAttempt).ConfigureAwait(false);
                if (!launchAttempt.CleanupConfirmed)
                {
                    await WriteLogAsync("normal rollback refused because the failed customized launch was not cleaned up exactly").ConfigureAwait(false);
                    return;
                }
            }

            if (launchedGeneration is not null && IsExactGenerationRunning(launchedGeneration))
            {
                await WriteLogAsync("normal rollback refused because the failed customized generation is still running").ConfigureAwait(false);
                return;
            }

            if (!await WaitForGenerationExitAsync(
                    sourceGeneration,
                    TimeSpan.FromSeconds(1),
                    rollbackTimeout.Token).ConfigureAwait(false))
            {
                await WriteLogAsync("normal rollback skipped because the original codex generation remains or cannot be proven absent").ConfigureAwait(false);
                return;
            }

            await EnsureNoCodexProcessForLaunchAsync(rollbackTimeout.Token).ConfigureAwait(false);
            var normalAttempt = new LaunchAttempt();
            var normal = await LaunchCodexAsync(executable, customized: false, 0, null, rollbackTimeout.Token, normalAttempt).ConfigureAwait(false);
            SetPreferredGeneration(normal);
            _normalGeneration = normal;
            await WriteLogAsync($"failed backdrop handoff rolled back once to normal codex: {GenerationKey(normal)}").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (operationCancellationToken.IsCancellationRequested)
        {
            await WriteLogAsync("normal rollback skipped because the handoff was canceled").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (rollbackTimeout.IsCancellationRequested)
        {
            await WriteLogAsync("normal rollback timed out safely; no replacement was launched").ConfigureAwait(false);
        }
        catch (Exception rollbackError)
        {
            await WriteLogAsync($"normal rollback failed safely: {rollbackError.Message}").ConfigureAwait(false);
        }
    }

    private static async Task<bool> WaitForGenerationExitAsync(
        CodexLaunchGeneration generation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requiredSamples = timeout <= TimeSpan.Zero ? 1 : 2;
        var cleanSamples = 0;
        var deadline = DateTimeOffset.UtcNow + timeout;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsGenerationConfirmedAbsent(WindowsCodexDiscovery.Capture(), generation))
            {
                cleanSamples++;
                if (cleanSamples >= requiredSamples) return true;
            }
            else
            {
                cleanSamples = 0;
            }

            if (timeout <= TimeSpan.Zero || DateTimeOffset.UtcNow >= deadline) break;
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        } while (DateTimeOffset.UtcNow < deadline);

        return cleanSamples >= requiredSamples &&
            IsGenerationConfirmedAbsent(WindowsCodexDiscovery.Capture(), generation);
    }

    private static bool IsGenerationConfirmedAbsent(
        CodexProcessInventory inventory,
        CodexLaunchGeneration generation)
    {
        if (inventory.HasUnverifiableCodexCandidate ||
            inventory.Processes.Any(process =>
                process is null ||
                process.Identity.ProcessId <= 0 ||
                process.Identity.StartTimeUtc == default ||
                process.Identity.SessionId != generation.Root.SessionId ||
                !string.Equals(process.Identity.UserSid, generation.Root.UserSid, StringComparison.OrdinalIgnoreCase) ||
                process.Role == CodexProcessRole.Unknown ||
                (process.Role == CodexProcessRole.Root && !process.HasVisibleWindow))) return false;

        var byIdentity = new Dictionary<CodexProcessIdentity, CodexProcessSnapshot>();
        foreach (var process in inventory.Processes)
        {
            if (!byIdentity.TryAdd(process.Identity, process)) return false;
            if (process.Identity == generation.Root) return false;
        }

        // A root-only check is insufficient: a helper may survive after the
        // recorded root exits. Walk every fresh parent chain and refuse to
        // claim absence until no chain can still reach that exact root.
        foreach (var process in inventory.Processes)
        {
            var current = process;
            var walked = new HashSet<CodexProcessIdentity>();
            while (current.ParentIdentity is not null)
            {
                if (!walked.Add(current.Identity)) return false;
                var parent = current.ParentIdentity;
                if (parent == generation.Root) return false;
                if (parent.ProcessId <= 0 ||
                    parent.StartTimeUtc == default ||
                    parent.SessionId != generation.Root.SessionId ||
                    !string.Equals(parent.UserSid, generation.Root.UserSid, StringComparison.OrdinalIgnoreCase) ||
                    parent.StartTimeUtc > current.Identity.StartTimeUtc ||
                    !byIdentity.TryGetValue(parent, out current)) return false;
                if (current.Role == CodexProcessRole.Unknown) return false;
            }
        }

        return true;
    }

    private async Task WaitForDevToolsAsync(
        int port,
        CodexLaunchGeneration generation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (timeout <= TimeSpan.Zero)
            throw new TimeoutException("codex opened, but its exact local backdrop connection did not become ready");
        var deadline = DateTimeOffset.UtcNow + timeout;
        var missedSamples = 0;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsDevToolsAvailable(port, generation)) return;
            // The wait is long enough for a slow first start; a launch that
            // died on startup must not hold the handoff for all of it.
            missedSamples = IsExactGenerationRunning(generation) ? 0 : missedSamples + 1;
            if (missedSamples >= 2)
                throw new InvalidOperationException("codex exited while it was starting");
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException("codex opened, but its exact local backdrop connection did not become ready");
    }

    private static void EnsurePortCanBeClaimed(int port, CodexLaunchGeneration source)
    {
        if (!IsPortListening(port)) return;
        if (WindowsCodexDiscovery.TryGetExclusiveLoopbackListenerOwner(port, out var owner) &&
            owner == source.Root.ProcessId &&
            WindowsCodexDiscovery.TryOpenExact(source.Root, source.Package, out var processLease) &&
            processLease is not null)
        {
            using (processLease) { }
            return;
        }

        throw new InvalidOperationException($"local port {port} belongs to another process • codex was left untouched");
    }

    private static bool IsPortListening(int port)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == port);
        }
        catch
        {
            return true;
        }
    }

    private static CodexLaunchGeneration RequireGeneration(CodexObservation observation)
    {
        if (!observation.IsRunning ||
            string.IsNullOrWhiteSpace(observation.GenerationKey) ||
            string.IsNullOrWhiteSpace(observation.PackageIdentity) ||
            observation.MainProcessId is null ||
            observation.MainProcessStartTimeUtc is null ||
            string.IsNullOrWhiteSpace(observation.ExecutablePath) ||
            string.IsNullOrWhiteSpace(observation.PackageFamilyName))
            throw new InvalidOperationException(observation.SafetyFailure ?? "codex does not have one verifiable process generation");

        var package = new CodexPackageContract(
            observation.PackageIdentity,
            observation.PackageFamilyName,
            observation.ExecutablePath);
        var inventory = WindowsCodexDiscovery.Capture();
        if (inventory.HasUnverifiableCodexCandidate)
            throw new InvalidOperationException("codex process ownership could not be fully verified • no automatic action");

        var current = inventory.Processes.FirstOrDefault(process =>
            process.Identity.ProcessId == observation.MainProcessId.Value &&
            process.Identity.StartTimeUtc == observation.MainProcessStartTimeUtc.Value);
        if (current is null ||
            current.Role != CodexProcessRole.Root ||
            current.ParentIdentity is not null ||
            !string.Equals(current.PackageIdentity, package.PackageIdentity, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(current.PublisherIdentity, package.PublisherIdentity, StringComparison.OrdinalIgnoreCase) ||
            !PathsEqual(current.ExecutablePath, package.ExecutablePath))
            throw new InvalidOperationException("codex process identity changed • no automatic action");

        var generation = new CodexLaunchGeneration(
            current.Identity,
            package);
        if (!string.Equals(GenerationKey(generation), observation.GenerationKey, StringComparison.Ordinal))
            throw new InvalidOperationException("codex observation no longer matches the current process generation");

        var selection = CodexProcessOwnership.SelectOwnedTargets(generation, inventory.Processes);
        if (!selection.Accepted)
            throw new InvalidOperationException($"codex generation is no longer safe to control: {DescribeOwnershipFailure(selection.Failure)}");
        return generation;
    }

    private static CodexPackageContract PackageContract(CodexProcessSnapshot process) =>
        new(process.PackageIdentity, process.PublisherIdentity, process.ExecutablePath);

    private static CodexPackageContract PackageContract(string executable)
    {
        var fullPath = Path.GetFullPath(executable);
        var packageIdentity = Directory.GetParent(fullPath)?.Parent?.Name;
        return new CodexPackageContract(packageIdentity, WindowsCodexDiscovery.OfficialPackageFamilyName, fullPath);
    }

    // Activation reports only a pid. A process that started before the request
    // cannot be the one it created, so a reused pid is never recorded.
    private static CodexProcessIdentity? TryCaptureActivatedIdentity(int processId, DateTimeOffset requestedUtc)
    {
        try
        {
            return WindowsCodexDiscovery.TryCaptureIdentity(processId, out var identity) &&
                identity is not null &&
                identity.StartTimeUtc >= requestedUtc - TimeSpan.FromSeconds(1)
                ? identity
                : null;
        }
        catch
        {
            return null;
        }
    }

    private string ResolveRendererProfile(BackdropSettings settings)
    {
        var configured = Environment.ExpandEnvironmentVariables(settings.RendererProfilePath);
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(paths.DataDirectory, "ChromiumProfile")
            : Path.GetFullPath(configured);
    }

    private static Task<string> ResolveCodexExecutableAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CodexPackageActivation.FindExecutable());
    }

    private CodexLaunchGeneration? GetPreferredGeneration()
    {
        lock (_generationLock) return _preferredGeneration;
    }

    private void SetPreferredGeneration(CodexLaunchGeneration generation)
    {
        lock (_generationLock) _preferredGeneration = generation;
    }

    private void ClearPreferredGeneration(CodexLaunchGeneration generation)
    {
        lock (_generationLock)
        {
            if (_preferredGeneration == generation) _preferredGeneration = null;
        }
    }

    private static CodexObservation UnsafeObservation(string failure) =>
        new(true, null, null, null, false, false, SafetyFailure: failure);

    private static string GenerationKey(CodexLaunchGeneration generation) =>
        $"{generation.Package.PackageIdentity}:{generation.Root.ProcessId}:{generation.Root.StartTimeUtc.UtcDateTime.Ticks}";

    private static string DescribeOwnershipFailure(CodexOwnershipFailure failure) => failure switch
    {
        CodexOwnershipFailure.InvalidPackageContract => "official package identity could not be verified",
        CodexOwnershipFailure.NoVisibleRoot => "no verified codex main window was found",
        CodexOwnershipFailure.AmbiguousVisibleRoots => "multiple codex main windows were found",
        CodexOwnershipFailure.UnverifiableIdentity => "a process identity changed or could not be verified",
        CodexOwnershipFailure.RecordedRootMissingOrReused => "the recorded codex process exited or its pid was reused",
        _ => "the exact codex generation could not be verified",
    };

    private static bool IsExpectedSocket(string? value, int port) =>
        Uri.TryCreate(value, UriKind.Absolute, out var socket) &&
        socket.Scheme is "ws" or "wss" &&
        socket.Port == port &&
        IPAddress.TryParse(socket.Host.Trim('[', ']'), out var address) &&
        IPAddress.IsLoopback(address) &&
        socket.AbsolutePath.StartsWith("/devtools/page/", StringComparison.Ordinal);

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

    private async Task RunSerializedAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await operation().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private void EnterLifecycleGate()
    {
        ThrowIfDisposed();
        _lifecycleGate.Wait();
        try
        {
            ThrowIfDisposed();
        }
        catch
        {
            _lifecycleGate.Release();
            throw;
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(RuntimeManager));
    }

    public Task WriteLogAsync(string message)
    {
        BackdropLog.Append(paths.LogPath, message);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            try { await _injector.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch { }
        }
        finally
        {
            _lifecycleGate.Release();
            _injector.Dispose();
            _probeHttp.Dispose();
        }
    }
}
