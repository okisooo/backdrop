using System.Text;
using System.Text.Json;

namespace CodexBackdrop;

public sealed record SupervisorStatus(string Text, bool Connected);

public sealed class CodexSupervisor : IAsyncDisposable
{
    private readonly RuntimeManager _runtime;
    private readonly AppPaths _paths;
    private readonly CodexSupervisorPolicy _policy;
    private readonly object _lifecycleSync = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly List<CancellationTokenSource> _retiredAttachmentCancellations = [];
    private CancellationTokenSource _attachmentCancellation = new();
    private TaskCompletionSource _operationsIdle = CompletedSource();
    private Task? _worker;
    private Task? _stopInjectorTask;
    private Task? _disposeTask;
    private int _activeOperations;
    private bool _enabled;
    private int _pauseVersion;
    private bool _persistenceAvailable;
    private bool _stateNeedsRepair;
    private bool _disposed;
    private bool _quiescent;
    private string? _lastStatus;

    public CodexSupervisor(RuntimeManager runtime, AppPaths paths, bool enabled)
    {
        _runtime = runtime;
        _paths = paths;
        var state = LoadState(paths.SupervisorStatePath, out var stateNeedsRepair, out var persistenceAvailable);
        _policy = new CodexSupervisorPolicy(state);
        _persistenceAvailable = persistenceAvailable;
        _stateNeedsRepair = stateNeedsRepair;
        _enabled = enabled && persistenceAvailable;
        if (!_enabled) _attachmentCancellation.Cancel();

        if (_stateNeedsRepair && _persistenceAvailable)
        {
            try
            {
                SaveState();
                _stateNeedsRepair = false;
            }
            catch
            {
                _persistenceAvailable = false;
                _enabled = false;
            }
        }
    }

    public event Action<SupervisorStatus>? StatusChanged;

    public bool Enabled => Volatile.Read(ref _enabled);

    /// <summary>
    /// Becomes true only after the worker, every serialized runtime operation,
    /// and the final injector stop have all reached a terminal state.
    /// </summary>
    public bool IsQuiescent => Volatile.Read(ref _quiescent);

    public void Start()
    {
        var persistenceUnavailable = false;
        lock (_lifecycleSync)
        {
            if (_disposed) return;
            if (!Volatile.Read(ref _persistenceAvailable))
            {
                persistenceUnavailable = true;
            }
            else
            {
                _worker ??= RunAsync(_cancellation.Token);
            }
        }

        if (persistenceUnavailable)
            Publish("supervisor state unavailable • automatic attachment paused", false);
    }

    public void SetEnabled(bool enabled)
    {
        var stateUnavailable = false;
        var changed = false;
        lock (_lifecycleSync)
        {
            if (_disposed) return;
            if (!enabled) _pauseVersion++;
            if (enabled && !Volatile.Read(ref _persistenceAvailable))
            {
                Volatile.Write(ref _enabled, false);
                CancelAttachmentLocked();
                stateUnavailable = true;
            }
            else
            {
                changed = Volatile.Read(ref _enabled) != enabled;
                Volatile.Write(ref _enabled, enabled);
                if (enabled) RenewAttachmentCancellationLocked();
                else CancelAttachmentLocked();
            }
        }

        if (!enabled) QueueInjectorStop();
        if (stateUnavailable)
        {
            Publish("supervisor state unavailable • automatic attachment paused", false);
            return;
        }
        if (!changed) return;
        Publish(enabled ? "watching for codex" : "automatic attachment paused", false);
    }

    public async Task RetryNowAsync(bool launchIfClosed = true, CancellationToken cancellationToken = default)
    {
        int pauseVersion;
        lock (_lifecycleSync) pauseVersion = _pauseVersion;
        await AcquireGateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsurePersistenceAvailable();
            lock (_lifecycleSync)
            {
                if (_disposed || pauseVersion != _pauseVersion)
                    throw new OperationCanceledException("attachment stopped before the queued retry could start");
                Volatile.Write(ref _enabled, true);
                RenewAttachmentCancellationLocked();
            }
            _policy.ClearForManualRetry();
            PersistStateOrPause();
            var observation = _runtime.ObserveCodex();
            if (!observation.IsRunning)
            {
                if (!launchIfClosed)
                {
                    Publish("watching for codex", false);
                    return;
                }

                Publish("opening codex with backdrop", false);
                using var launchCancellation = CreateOperationCancellation(cancellationToken, includeAttachment: true);
                try
                {
                    launchCancellation.Token.ThrowIfCancellationRequested();
                    await _runtime.LaunchCustomizedAsync(launchCancellation.Token).ConfigureAwait(false);
                    if (Volatile.Read(ref _disposed) || !Volatile.Read(ref _enabled))
                    {
                        await _runtime.StopInjectorAsync().ConfigureAwait(false);
                        Publish("automatic attachment paused", false);
                        throw new OperationCanceledException("automatic attachment paused", launchCancellation.Token);
                    }
                    launchCancellation.Token.ThrowIfCancellationRequested();
                    _policy.MarkSuccess(RequireHealthyObservation());
                    PersistStateOrPause();
                    Publish("backdrop attached", true);
                }
                catch (Exception error) when (error is not OperationCanceledException || !launchCancellation.IsCancellationRequested)
                {
                    throw await RecoverAttachmentFailureAsync(error, observation, launchCancellation.Token).ConfigureAwait(false);
                }
                return;
            }

            var decision = _policy.Decide(observation, true, DateTimeOffset.UtcNow, force: true);
            if (decision.Action == SupervisorAction.None && !decision.Connected)
                throw new InvalidOperationException(decision.Status);
            await ExecuteDecisionAsync(observation, decision, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseGate();
        }
    }

    public async Task OpenNormallyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetEnabled(false);
        await AcquireGateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _disposed)) return;
            using var operationCancellation = CreateOperationCancellation(cancellationToken, includeAttachment: false);
            await _runtime.LaunchNormalAsync(operationCancellation.Token).ConfigureAwait(false);
            Publish("backdrop paused • codex opened normally", false);
        }
        finally
        {
            ReleaseGate();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && Volatile.Read(ref _persistenceAvailable))
        {
            var pollDelay = 1000;
            try
            {
                if (await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                {
                    try
                    {
                        if (cancellationToken.IsCancellationRequested || Volatile.Read(ref _disposed)) break;
                        var observation = _runtime.ObserveCodex();
                        var decision = _policy.Decide(observation, Volatile.Read(ref _enabled), DateTimeOffset.UtcNow);
                        await ExecuteDecisionAsync(observation, decision).ConfigureAwait(false);
                        if (decision.Connected && decision.Action == SupervisorAction.None) pollDelay = 3000;
                    }
                    finally
                    {
                        _gate.Release();
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                Publish("supervisor waiting • " + error.Message, false);
                await WriteLogSafelyAsync("supervisor error: " + error.Message).ConfigureAwait(false);
            }

            if (!Volatile.Read(ref _persistenceAvailable)) break;
            try { await Task.Delay(pollDelay, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private CodexObservation RequireHealthyObservation()
    {
        var observation = _runtime.ObserveCodex();
        if (!observation.IsRunning || !observation.InjectorHealthy || !observation.DevToolsAvailable
            || string.IsNullOrWhiteSpace(observation.GenerationKey)
            || !string.IsNullOrWhiteSpace(observation.SafetyFailure))
            throw new InvalidOperationException("the backdrop did not remain healthy after attachment");
        return observation;
    }

    private async Task ExecuteDecisionAsync(
        CodexObservation observation,
        SupervisorDecision decision,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Publish(decision.Status, decision.Connected);
        if (decision.Action == SupervisorAction.None) return;

        _policy.MarkAttempt(observation, decision.Action);
        try
        {
            PersistStateOrPause();
        }
        catch (OperationCanceledException) when (
            _cancellation.IsCancellationRequested ||
            !Volatile.Read(ref _enabled) ||
            Volatile.Read(ref _disposed))
        {
            Publish("automatic attachment paused", false);
            await WriteLogSafelyAsync("supervisor attachment canceled safely").ConfigureAwait(false);
            throw;
        }
        catch (Exception error)
        {
            Publish("attachment paused • " + error.Message, false);
            await WriteLogSafelyAsync("supervisor state save failed before attachment: " + error.Message).ConfigureAwait(false);
            throw;
        }

        using var operationCancellation = CreateOperationCancellation(cancellationToken, includeAttachment: true);
        try
        {
            await _runtime.StartAsync(
                restartCodex: decision.Action == SupervisorAction.Handoff,
                observation,
                operationCancellation.Token).ConfigureAwait(false);
            if (Volatile.Read(ref _disposed) || !Volatile.Read(ref _enabled))
            {
                await _runtime.StopInjectorAsync().ConfigureAwait(false);
                Publish("automatic attachment paused", false);
                throw new OperationCanceledException("automatic attachment paused", operationCancellation.Token);
            }

            operationCancellation.Token.ThrowIfCancellationRequested();
            _policy.MarkSuccess(RequireHealthyObservation());
            try
            {
                PersistStateOrPause();
            }
            catch (Exception error)
            {
                Publish("backdrop attached • state unavailable; automatic attachment paused", false);
                await WriteLogSafelyAsync("supervisor state save failed after attachment: " + error.Message).ConfigureAwait(false);
                throw;
            }
            Publish("backdrop attached", true);
        }
        catch (OperationCanceledException) when (
            operationCancellation.IsCancellationRequested ||
            _cancellation.IsCancellationRequested ||
            !Volatile.Read(ref _enabled) ||
            Volatile.Read(ref _disposed))
        {
            Publish("automatic attachment paused", false);
            await WriteLogSafelyAsync("supervisor attachment canceled safely").ConfigureAwait(false);
            throw;
        }
        catch (Exception error)
        {
            throw await RecoverAttachmentFailureAsync(error, observation, operationCancellation.Token,
                restoreNormal: decision.Action == SupervisorAction.Handoff).ConfigureAwait(false);
        }
    }

    private async Task<Exception> RecoverAttachmentFailureAsync(
        Exception error, CodexObservation attempted, CancellationToken cancellationToken, bool restoreNormal = true)
    {
        var message = (restoreNormal
            ? "bd001 • backdrop failed; normal codex restored • "
            : "bd004 • backdrop failed; existing codex left open • ") + error.Message;
        try
        {
            using var recovery = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            recovery.CancelAfter(TimeSpan.FromSeconds(45));
            // ponytail: only a launch/handoff owns recovery; in-place failure must not close a user's session.
            if (restoreNormal)
                await _runtime.RecoverNormalAfterFailureAsync(attempted, recovery.Token).ConfigureAwait(false);
            else
                await _runtime.StopInjectorAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            message = "bd003 • attachment stopped; normal recovery cancelled • " + error.Message;
        }
        catch (Exception recoveryError)
        {
            message = "bd002 • backdrop failed; normal recovery needs attention • " + recoveryError.Message;
        }
        if (!attempted.IsRunning)
        {
            // A fresh launch has no package identity until launch or recovery completes.
            _policy.MarkAttempt(_runtime.ObserveCodex(), SupervisorAction.None);
        }
        _policy.MarkFailure(message);
        try { PersistStateOrPause(); }
        catch { }
        Publish(message, false);
        await WriteLogSafelyAsync(message + "; cause: " + error.Message).ConfigureAwait(false);
        return new InvalidOperationException(message, error);
    }

    private void Publish(string text, bool connected)
    {
        Action<SupervisorStatus>? handler;
        lock (_lifecycleSync)
        {
            var key = $"{connected}:{text}";
            if (_lastStatus == key) return;
            _lastStatus = key;
            handler = StatusChanged;
        }

        try { handler?.Invoke(new SupervisorStatus(text, connected)); }
        catch (Exception error) { _ = WriteLogSafelyAsync("status callback failed: " + error.Message); }
    }

    private static SupervisorPersistentState LoadState(
        string path,
        out bool stateNeedsRepair,
        out bool persistenceAvailable)
    {
        stateNeedsRepair = false;
        persistenceAvailable = true;
        var backupPath = path + ".bak";
        var primaryExists = File.Exists(path);
        var backupExists = File.Exists(backupPath);

        if (primaryExists)
        {
            try { return ReadState(path); }
            catch { }
        }

        if (backupExists)
        {
            try
            {
                stateNeedsRepair = true;
                return ReadState(backupPath);
            }
            catch { }
        }

        // A missing pair is a normal first run. If either file exists but is
        // unreadable or malformed, refuse to supervise rather than guessing.
        if (primaryExists || backupExists) persistenceAvailable = false;
        return new();
    }

    private static SupervisorPersistentState ReadState(string path)
    {
        var state = JsonSerializer.Deserialize<SupervisorPersistentState>(File.ReadAllText(path), JsonOptions());
        return state ?? throw new InvalidDataException("supervisor state is empty");
    }

    private void SaveState()
    {
        var path = _paths.SupervisorStatePath;
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("supervisor state path has no parent directory");

        Directory.CreateDirectory(directory);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        var recoveryBackup = _stateNeedsRepair
            ? $"{path}.bak.recovery.{Guid.NewGuid():N}"
            : path + ".bak";
        try
        {
            var json = JsonSerializer.Serialize(_policy.State, JsonOptions());
            using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                options: FileOptions.WriteThrough))
            {
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true))
                {
                    writer.Write(json);
                    writer.Flush();
                }
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                File.Replace(temporary, path, recoveryBackup, ignoreMetadataErrors: false);
                if (_stateNeedsRepair) TryDelete(recoveryBackup);
            }
            else
            {
                // The temporary file is in the same directory, so this is an
                // atomic rename for the first state write.
                File.Move(temporary, path);
            }

            _stateNeedsRepair = false;
        }
        finally { TryDelete(temporary); }
    }

    private void EnsurePersistenceAvailable()
    {
        if (!Volatile.Read(ref _persistenceAvailable))
            throw new InvalidOperationException("supervisor state is unavailable; automatic attachment is paused");
    }

    private void PersistStateOrPause()
    {
        EnsurePersistenceAvailable();
        try
        {
            SaveState();
        }
        catch (Exception error)
        {
            Volatile.Write(ref _persistenceAvailable, false);
            Volatile.Write(ref _enabled, false);
            lock (_lifecycleSync) CancelAttachmentLocked();
            QueueInjectorStop();
            throw new IOException("supervisor state could not be saved; automatic attachment is paused", error);
        }
    }

    private void QueueInjectorStop()
    {
        lock (_lifecycleSync)
        {
            if (_disposed || _stopInjectorTask is { IsCompleted: false }) return;
            _stopInjectorTask = StopInjectorWhenIdleAsync(_cancellation.Token);
        }
    }

    private CancellationToken GetAttachmentCancellationToken()
    {
        lock (_lifecycleSync) return _attachmentCancellation.Token;
    }

    private void RenewAttachmentCancellationLocked()
    {
        if (!_attachmentCancellation.IsCancellationRequested) return;
        var previous = _attachmentCancellation;
        _attachmentCancellation = new CancellationTokenSource();
        _retiredAttachmentCancellations.Add(previous);
    }

    private void CancelAttachmentLocked()
    {
        try { _attachmentCancellation.Cancel(); } catch { }
    }

    private async Task StopInjectorWhenIdleAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!Volatile.Read(ref _enabled)) await _runtime.StopInjectorAsync().ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) { await WriteLogSafelyAsync("queued injector stop failed: " + error.Message).ConfigureAwait(false); }
    }

    private async Task AcquireGateAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycleSync)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CodexSupervisor));
            if (_activeOperations++ == 0)
                _operationsIdle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _cancellation.Token,
            cancellationToken);
        try
        {
            await _gate.WaitAsync(waitCancellation.Token).ConfigureAwait(false);
        }
        catch
        {
            CompleteOperation();
            throw;
        }
    }

    private void ReleaseGate()
    {
        try { _gate.Release(); }
        finally { CompleteOperation(); }
    }

    private void CompleteOperation()
    {
        TaskCompletionSource? idle = null;
        lock (_lifecycleSync)
        {
            if (--_activeOperations == 0) idle = _operationsIdle;
        }
        idle?.TrySetResult();
    }

    private CancellationTokenSource CreateOperationCancellation(
        CancellationToken cancellationToken,
        bool includeAttachment)
    {
        if (includeAttachment)
        {
            return CancellationTokenSource.CreateLinkedTokenSource(
                _cancellation.Token,
                GetAttachmentCancellationToken(),
                cancellationToken);
        }

        return CancellationTokenSource.CreateLinkedTokenSource(
            _cancellation.Token,
            cancellationToken);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    private async Task WriteLogSafelyAsync(string message)
    {
        try { await _runtime.WriteLogAsync(message).ConfigureAwait(false); }
        catch { }
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleSync)
        {
            if (_disposeTask is not null) return new ValueTask(_disposeTask);
            _disposed = true;
            Volatile.Write(ref _enabled, false);
            CancelAttachmentLocked();
            _cancellation.Cancel();
            _disposeTask = DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        var operationsCompleted = true;
        var cleanupCompleted = true;
        try
        {
            if (_worker is not null)
            {
                try { await _worker.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    operationsCompleted = false;
                    await WriteLogSafelyAsync("supervisor worker did not stop before the shutdown deadline").ConfigureAwait(false);
                }
                catch (Exception error) { await WriteLogSafelyAsync("supervisor stopped with error: " + error.Message).ConfigureAwait(false); }
            }

            Task operationsIdle;
            lock (_lifecycleSync)
            {
                operationsIdle = _activeOperations == 0 ? Task.CompletedTask : _operationsIdle.Task;
            }
            try
            {
                await operationsIdle.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                operationsCompleted = false;
                await WriteLogSafelyAsync("supervisor shutdown timed out waiting for an active operation; process exit will finish containment").ConfigureAwait(false);
            }

            Task? stopInjectorTask;
            lock (_lifecycleSync) stopInjectorTask = _stopInjectorTask;
            if (stopInjectorTask is not null)
            {
                try { await stopInjectorTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch (Exception error)
                {
                    cleanupCompleted = false;
                    await WriteLogSafelyAsync("queued injector stop failed: " + error.Message).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (operationsCompleted)
            {
                try { await _runtime.StopInjectorAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch (Exception error)
                {
                    cleanupCompleted = false;
                    await WriteLogSafelyAsync("final injector stop failed: " + error.Message).ConfigureAwait(false);
                }
            }
            else
            {
                cleanupCompleted = false;
                await WriteLogSafelyAsync("final injector stop deferred until the active operation reaches a terminal state").ConfigureAwait(false);
            }

            var quiescent = operationsCompleted && cleanupCompleted;
            Volatile.Write(ref _quiescent, quiescent);
            if (quiescent)
            {
                foreach (var retired in _retiredAttachmentCancellations) retired.Dispose();
                _retiredAttachmentCancellations.Clear();
                _attachmentCancellation.Dispose();
                _cancellation.Dispose();
                _gate.Dispose();
            }
        }
    }
}
