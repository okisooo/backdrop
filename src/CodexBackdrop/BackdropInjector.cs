using System.Net.Http;
using System.Net.WebSockets;
using System.Net;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexBackdrop;

public sealed class BackdropInjector(AppPaths paths) : IDisposable
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TargetTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TargetListTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan GenerationValidationTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ActivePollDelay = TimeSpan.FromMilliseconds(650);
    private static readonly TimeSpan StablePollDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan UnresponsivePollDelayLimit = TimeSpan.FromSeconds(10);
    private const int MaximumHttpResponseBytes = 1 * 1024 * 1024;
    private const int MaximumWebSocketFrameBytes = 64 * 1024;
    private const int MaximumWebSocketMessageBytes = 16 * 1024 * 1024;
    private const long MaximumMediaBytes = 128 * 1024 * 1024;

    private readonly HttpClient _http = new() { Timeout = TargetListTimeout };
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _stateSync = new();
    private CancellationTokenSource? _cancellation;
    private Task? _worker;
    private TaskCompletionSource? _firstHealthy;
    private Task? _stopTask;
    private CancellationTokenSource? _cancelledSource;
    private Task? _cancelTask;
    private CancellationTokenSource? _disposedCancellation;
    private DateTimeOffset _lastHealthyUtc = DateTimeOffset.MinValue;
    private TaskCompletionSource _wake = NewWake();
    private int _generation;
    private int _disposed;

    public bool IsRunning => _worker is { IsCompleted: false } && _cancellation is { IsCancellationRequested: false };

    public bool IsHealthy => IsRunning
        && DateTimeOffset.UtcNow - _lastHealthyUtc < StablePollDelay + TimeSpan.FromSeconds(3);

    /// <summary>Ends the current poll wait, so a settings change shows at once.</summary>
    public void Nudge() => Volatile.Read(ref _wake).TrySetResult();

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task StartAsync(
        int port,
        TimeSpan timeout,
        Func<CancellationToken, ValueTask<bool>> generationValidator,
        CancellationToken cancellationToken = default)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        ArgumentNullException.ThrowIfNull(generationValidator);
        ThrowIfDisposed();

        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        TaskCompletionSource firstHealthy;
        CancellationTokenSource cancellation;
        var generation = 0;
        var publication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? worker = null;
        var accepted = false;
        try
        {
            await StopCurrentAsync().ConfigureAwait(false);
            ThrowIfDisposed();
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            firstHealthy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_stateSync)
            {
                if (_disposed == 0)
                {
                    generation = Interlocked.Increment(ref _generation);
                    _lastHealthyUtc = DateTimeOffset.MinValue;
                    // Keep the worker dormant until the generation and its task are
                    // published together; StopAsync may run without the lifecycle gate.
                    worker = RunAsync(
                        port,
                        generation,
                        cancellation,
                        firstHealthy,
                        generationValidator,
                        publication.Task);
                    _firstHealthy = firstHealthy;
                    _cancellation = cancellation;
                    _worker = worker;
                    _stopTask = null;
                    accepted = true;
                }
            }
            if (!accepted)
            {
                publication.TrySetResult();
                var cancelTask = RequestCancellation(cancellation, allowDetached: true);
                ScheduleStopCleanup(cancellation, worker, firstHealthy, cancelTask);
                ThrowIfDisposed();
            }
            else publication.TrySetResult();
        }
        finally { _lifecycleGate.Release(); }

        try { await firstHealthy.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await StopGenerationAsync(generation, cancellation, firstHealthy).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await StopGenerationAsync(generation, cancellation, firstHealthy).ConfigureAwait(false);
            if (firstHealthy.Task.IsCanceled)
                throw new TimeoutException("codex opened, but the backdrop did not pass its compatibility checks");
            throw;
        }
    }

    public void Stop()
    {
        try { StopAsync().GetAwaiter().GetResult(); }
        catch { }
    }

    public Task StopAsync()
    {
        var deadline = StopDeadline();
        StopSnapshot snapshot;
        TaskCompletionSource completion;
        lock (_stateSync)
        {
            if (_stopTask is not null) return _stopTask;
            snapshot = new StopSnapshot(
                Volatile.Read(ref _generation),
                _cancellation,
                _worker,
                _firstHealthy,
                deadline);
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopTask = completion.Task;
        }

        _ = RunStopAsync(snapshot, completion);
        return completion.Task;
    }

    /// <summary>
    /// Takes the backdrop out of every Codex page so Codex looks exactly as it
    /// does without it. The worker must be stopped first, or it re-applies.
    /// </summary>
    public async Task RemoveAsync(int port, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync($"http://127.0.0.1:{port}/json/list", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (body.Length > MaximumHttpResponseBytes)
            throw new InvalidDataException("codex returned an oversized renderer target list");

        foreach (var target in JsonSerializer.Deserialize<Target[]>(body, JsonOptions()) ?? [])
        {
            if (!IsExpectedTarget(target) || !TryNormalizeTarget(target, port, out var normalized)) continue;
            try
            {
                using var session = new CdpSession(normalized.WebSocketDebuggerUrl!, cancellationToken, _ => ValueTask.CompletedTask);
                await session.ConnectAsync().ConfigureAwait(false);
                await session.CommandAsync("Runtime.evaluate", new { expression = RemoveExpression }).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // A page that closed meanwhile has nothing left to remove.
            }
        }
    }

    private const string RemoveExpression = """
        (() => {
          window.__codexBackdropSentry?.disconnect?.();
          for (const still of document.querySelectorAll('#codex-backdrop-layer [data-codex-backdrop-still]')) URL.revokeObjectURL(still.dataset.codexBackdropStill);
          for (const node of document.querySelectorAll('#codex-backdrop-style,#codex-backdrop-layer')) node.remove();
          if (window.__codexBackdropMedia?.url) URL.revokeObjectURL(window.__codexBackdropMedia.url);
          for (const key of ['__codexBackdropSentry', '__codexBackdrop', '__codexBackdropMedia', '__codexBackdropTransfer']) delete window[key];
          return true;
        })()
        """;

    private async Task StopGenerationAsync(int generation, CancellationTokenSource cancellation, TaskCompletionSource firstHealthy)
    {
        var deadline = StopDeadline();
        Task? worker;
        lock (_stateSync)
            worker = IsCurrentState(generation, cancellation) ? _worker : null;

        if (!await _lifecycleGate.WaitAsync(Remaining(deadline)).ConfigureAwait(false))
        {
            await StopSnapshotWithoutGateAsync(
                new StopSnapshot(generation, cancellation, worker, firstHealthy, deadline)).ConfigureAwait(false);
            return;
        }

        try
        {
            if (generation != Volatile.Read(ref _generation) || !ReferenceEquals(_cancellation, cancellation)) return;
            await StopCurrentAsync(generation, cancellation, deadline).ConfigureAwait(false);
        }
        finally { _lifecycleGate.Release(); }
    }

    private async Task StopCurrentAsync()
    {
        await StopCurrentAsync(null, null, StopDeadline()).ConfigureAwait(false);
    }

    private async Task StopCurrentAsync(int? expectedGeneration, CancellationTokenSource? expectedCancellation, long deadline)
    {
        CancellationTokenSource? cancellation;
        Task? worker;
        TaskCompletionSource? firstHealthy;
        lock (_stateSync)
        {
            if (expectedGeneration is not null && !IsCurrentState(expectedGeneration.Value, expectedCancellation)) return;
            Interlocked.Increment(ref _generation);
            _lastHealthyUtc = DateTimeOffset.MinValue;
            firstHealthy = _firstHealthy;
            cancellation = _cancellation;
            worker = _worker;
        }

        firstHealthy?.TrySetCanceled();
        if (cancellation is null && worker is null) return;
        var cancelTask = RequestCancellation(cancellation);
        if (worker is not null && !worker.IsCompleted)
        {
            try { await worker.WaitAsync(Remaining(deadline)).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                ScheduleStopCleanup(cancellation, worker, firstHealthy, cancelTask);
                throw new TimeoutException("the previous backdrop connection did not stop cleanly");
            }
        }

        ScheduleStopCleanup(cancellation, worker, firstHealthy, cancelTask);
    }

    private async Task RunStopAsync(StopSnapshot snapshot, TaskCompletionSource completion)
    {
        try
        {
            var deadline = snapshot.Deadline;
            if (await _lifecycleGate.WaitAsync(Remaining(deadline)).ConfigureAwait(false))
            {
                try { await StopCurrentAsync(snapshot.Generation, snapshot.Cancellation, deadline).ConfigureAwait(false); }
                finally { _lifecycleGate.Release(); }
            }
            else
            {
                await StopSnapshotWithoutGateAsync(snapshot).ConfigureAwait(false);
            }
            completion.TrySetResult();
        }
        catch (Exception error)
        {
            completion.TrySetException(error);
        }
    }

    private async Task StopSnapshotWithoutGateAsync(StopSnapshot snapshot)
    {
        var deadline = snapshot.Deadline;
        lock (_stateSync)
        {
            if (!IsCurrentState(snapshot.Generation, snapshot.Cancellation)) return;
            Interlocked.Increment(ref _generation);
            _lastHealthyUtc = DateTimeOffset.MinValue;
        }

        snapshot.FirstHealthy?.TrySetCanceled();
        var cancelTask = RequestCancellation(snapshot.Cancellation);
        if (snapshot.Worker is null || snapshot.Worker.IsCompleted)
        {
            ScheduleStopCleanup(snapshot.Cancellation, snapshot.Worker, snapshot.FirstHealthy, cancelTask);
            return;
        }

        try { await snapshot.Worker.WaitAsync(Remaining(deadline)).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            ScheduleStopCleanup(snapshot.Cancellation, snapshot.Worker, snapshot.FirstHealthy, cancelTask);
            throw new TimeoutException("the previous backdrop connection did not stop cleanly");
        }

        ScheduleStopCleanup(snapshot.Cancellation, snapshot.Worker, snapshot.FirstHealthy, cancelTask);
    }

    private Task RequestCancellation(CancellationTokenSource? cancellation, bool allowDetached = false)
    {
        if (cancellation is null) return Task.CompletedTask;
        lock (_stateSync)
        {
            if (ReferenceEquals(_cancelledSource, cancellation)) return _cancelTask ?? Task.CompletedTask;
            if (!allowDetached && !ReferenceEquals(_cancellation, cancellation)) return Task.CompletedTask;
            _cancelledSource = cancellation;
            _cancelTask = CancelSourceAsync(cancellation);
            return _cancelTask;
        }
    }

    private static async Task CancelSourceAsync(CancellationTokenSource cancellation)
    {
        try { await cancellation.CancelAsync().ConfigureAwait(false); }
        catch { }
    }

    private void ScheduleStopCleanup(
        CancellationTokenSource? cancellation,
        Task? worker,
        TaskCompletionSource? firstHealthy,
        Task cancelTask)
    {
        _ = CompleteStopCleanupAsync(cancellation, worker, firstHealthy, cancelTask);
    }

    private async Task CompleteStopCleanupAsync(
        CancellationTokenSource? cancellation,
        Task? worker,
        TaskCompletionSource? firstHealthy,
        Task cancelTask)
    {
        if (worker is not null)
        {
            try { await worker.ConfigureAwait(false); }
            catch { }
        }
        try { await cancelTask.ConfigureAwait(false); }
        catch { }

        lock (_stateSync)
        {
            if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
            if (ReferenceEquals(_worker, worker)) _worker = null;
            if (ReferenceEquals(_firstHealthy, firstHealthy)) _firstHealthy = null;
        }
        DisposeCancellation(cancellation);
    }

    private void DisposeCancellation(CancellationTokenSource? cancellation)
    {
        if (cancellation is null) return;
        lock (_stateSync)
        {
            if (ReferenceEquals(_disposedCancellation, cancellation)) return;
            _disposedCancellation = cancellation;
        }
        try { cancellation.Dispose(); }
        catch { }
    }

    private bool IsCurrentState(int generation, CancellationTokenSource? cancellation) =>
        generation == Volatile.Read(ref _generation) && ReferenceEquals(_cancellation, cancellation);

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(BackdropInjector));
    }

    private static long StopDeadline() =>
        Stopwatch.GetTimestamp() + (long)(StopTimeout.TotalSeconds * Stopwatch.Frequency);

    private static TimeSpan Remaining(long deadline)
    {
        var remaining = deadline - Stopwatch.GetTimestamp();
        if (remaining <= 0) return TimeSpan.Zero;
        var ticks = (long)Math.Ceiling(remaining * (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency);
        return TimeSpan.FromTicks(Math.Max(1, ticks));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Task stopTask;
        try { stopTask = StopAsync(); }
        catch
        {
            try { _http.Dispose(); } catch { }
            return;
        }
        _ = DisposeHttpAfterStopAsync(stopTask);
    }

    private async Task DisposeHttpAfterStopAsync(Task stopTask)
    {
        try { await stopTask.ConfigureAwait(false); }
        catch { }
        try { _http.Dispose(); }
        catch { }
    }

    private async Task RunAsync(
        int port,
        int generation,
        CancellationTokenSource cancellation,
        TaskCompletionSource firstHealthy,
        Func<CancellationToken, ValueTask<bool>> generationValidator,
        Task publication)
    {
        await publication.ConfigureAwait(false);
        var cancellationToken = cancellation.Token;
        string previousSignature = "";
        string previousError = "";
        string previousTargetError = "";
        string previousTargetSet = "";
        var previousPollWasStable = false;
        var unresponsivePolls = 0;
        InjectionState? cachedState = null;
        var nextPollDelay = ActivePollDelay;
        var lastErrorLogged = DateTimeOffset.MinValue;
        try
        {
            while (IsCurrentGeneration(generation, cancellationToken))
            {
                try
                {
                    await EnsureGenerationAsync(
                        generation,
                        generationValidator,
                        cancellationToken,
                        cancellationToken).ConfigureAwait(false);
                    var state = await LoadStateAsync(cancellationToken, cachedState).ConfigureAwait(false);
                    cachedState = state;
                    var targets = await ListTargetsAsync(
                        port,
                        generation,
                        generationValidator,
                        cancellationToken).ConfigureAwait(false);
                    var applied = 0;
                    var healthyTargets = 0;
                    var startingTargets = 0;
                    var unresponsiveTargets = 0;
                    var targetErrors = new List<string>();
                    foreach (var target in targets)
                    {
                        if (!IsCurrentGeneration(generation, cancellationToken)) break;
                        try
                        {
                            if (await ApplyTargetAsync(
                                target,
                                state,
                                generation,
                                generationValidator,
                                cancellationToken).ConfigureAwait(false)) applied++;
                            healthyTargets++;
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                        catch (GenerationValidationException) { throw; }
                        catch (RendererStartingException) { startingTargets++; }
                        catch (Exception error)
                        {
                            if (error is TimeoutException) unresponsiveTargets++;
                            targetErrors.Add($"{target.Id}: {error.Message}");
                        }
                    }

                    if (!IsCurrentGeneration(generation, cancellationToken)) break;

                    if (targetErrors.Count > 0)
                    {
                        var targetError = string.Join("; ", targetErrors);
                        if (targetError != previousTargetError || DateTimeOffset.UtcNow - lastErrorLogged >= TimeSpan.FromSeconds(30))
                        {
                            await LogAsync("renderer target failure: " + targetError).ConfigureAwait(false);
                            previousTargetError = targetError;
                            lastErrorLogged = DateTimeOffset.UtcNow;
                        }
                    }
                    else previousTargetError = "";

                    if (healthyTargets > 0)
                    {
                        _lastHealthyUtc = DateTimeOffset.UtcNow;
                        firstHealthy.TrySetResult();
                        previousError = "";
                        unresponsivePolls = 0;
                    }
                    else if (targets.Length > 0 && targetErrors.Count > 0)
                    {
                        // A busy or frozen renderer (a minimized window, for one)
                        // answers nothing; back off instead of reconnecting every poll.
                        unresponsivePolls = unresponsiveTargets == targetErrors.Count ? unresponsivePolls + 1 : 0;
                        throw new InvalidOperationException("no renderer target accepted the backdrop");
                    }
                    else if (startingTargets > 0)
                    {
                        unresponsivePolls = 0;
                        throw new InvalidOperationException("codex is still starting");
                    }

                    if (applied > 0 || state.Signature != previousSignature)
                        await LogAsync($"applied {state.MediaKind} backdrop to {applied} renderer(s)").ConfigureAwait(false);

                    var targetSet = TargetSetSignature(targets);
                    var stateChanged = state.Signature != previousSignature;
                    var targetSetChanged = targetSet != previousTargetSet;
                    var allTargetsHealthy = targets.Length > 0
                        && healthyTargets == targets.Length
                        && targetErrors.Count == 0;
                    var pollIsStable = allTargetsHealthy
                        && applied == 0
                        && !stateChanged
                        && !targetSetChanged;
                    var pollDelay = previousPollWasStable && pollIsStable
                        ? StablePollDelay
                        : ActivePollDelay;
                    previousSignature = state.Signature;
                    previousTargetSet = targetSet;
                    previousPollWasStable = pollIsStable;
                    nextPollDelay = pollDelay;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (GenerationValidationException error)
                {
                    _ = RequestCancellation(cancellation);
                    firstHealthy.TrySetException(error);
                    break;
                }
                catch (Exception error)
                {
                    previousPollWasStable = false;
                    nextPollDelay = unresponsivePolls == 0
                        ? ActivePollDelay
                        : TimeSpan.FromMilliseconds(Math.Min(
                            UnresponsivePollDelayLimit.TotalMilliseconds,
                            ActivePollDelay.TotalMilliseconds * Math.Pow(2, unresponsivePolls)));
                    var now = DateTimeOffset.UtcNow;
                    if (error.Message != previousError || now - lastErrorLogged >= TimeSpan.FromSeconds(30))
                    {
                        await LogAsync("injector waiting: " + error.Message).ConfigureAwait(false);
                        previousError = error.Message;
                        lastErrorLogged = now;
                    }
                }

                var wake = Volatile.Read(ref _wake);
                await Task.WhenAny(Task.Delay(nextPollDelay, cancellationToken), wake.Task).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested) break;
                if (wake.Task.IsCompleted) Interlocked.CompareExchange(ref _wake, NewWake(), wake);
            }
        }
        finally
        {
            if (generation == Volatile.Read(ref _generation))
            {
                _lastHealthyUtc = DateTimeOffset.MinValue;
                if (!firstHealthy.Task.IsCompleted)
                    firstHealthy.TrySetCanceled();
            }
        }
    }

    private bool IsCurrentGeneration(int generation, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested && generation == Volatile.Read(ref _generation);

    private async ValueTask EnsureGenerationAsync(
        int generation,
        Func<CancellationToken, ValueTask<bool>> generationValidator,
        CancellationToken workerCancellationToken,
        CancellationToken operationCancellationToken)
    {
        if (!IsCurrentGeneration(generation, workerCancellationToken))
            throw new OperationCanceledException(workerCancellationToken);
        if (operationCancellationToken.IsCancellationRequested)
            throw new OperationCanceledException(operationCancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            workerCancellationToken,
            operationCancellationToken);
        timeout.CancelAfter(GenerationValidationTimeout);

        bool valid;
        try
        {
            valid = await generationValidator(timeout.Token)
                .AsTask()
                .WaitAsync(timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (workerCancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(workerCancellationToken);
        }
        catch (OperationCanceledException) when (operationCancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException error)
        {
            throw new GenerationValidationException("codex generation validation timed out", error);
        }
        catch (Exception error)
        {
            throw new GenerationValidationException("codex generation validation failed", error);
        }

        if (!valid)
        {
            if (workerCancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(workerCancellationToken);
            if (operationCancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(operationCancellationToken);
            throw new GenerationValidationException("codex generation is no longer the recorded process");
        }

        if (!IsCurrentGeneration(generation, workerCancellationToken))
            throw new OperationCanceledException(workerCancellationToken);
        if (operationCancellationToken.IsCancellationRequested)
            throw new OperationCanceledException(operationCancellationToken);
    }

    private async Task<InjectionState> LoadStateAsync(
        CancellationToken cancellationToken,
        InjectionState? cachedState)
    {
        const string themeName = "theme.css";
        const string selectorName = "codex-selectors.css";
        var configFingerprint = Fingerprint(paths.ConfigPath);
        var themeFingerprint = RuntimeFingerprint(themeName);
        var selectorFingerprint = RuntimeFingerprint(selectorName);

        if (cachedState is not null && configFingerprint == cachedState.ConfigFingerprint)
        {
            var mediaFingerprint = Fingerprint(cachedState.Settings.BackgroundPath);
            var themeChanged = themeFingerprint != cachedState.ThemeFingerprint;
            var selectorsChanged = selectorFingerprint != cachedState.SelectorFingerprint;
            var mediaChanged = mediaFingerprint != cachedState.MediaFingerprint;
            if (!themeChanged && !selectorsChanged && !mediaChanged) return cachedState;

            var mediaPath = cachedState.Settings.EffectiveBackgroundPath;
            if (!themeChanged && !selectorsChanged)
            {
                var media = MediaDescriptor.FromPath(mediaPath);
                return cachedState with
                {
                    MediaPath = mediaPath,
                    MediaKind = media.Kind,
                    MimeType = media.MimeType,
                    Signature = BuildSignature(
                        configFingerprint,
                        themeFingerprint,
                        selectorFingerprint,
                        mediaFingerprint),
                    MediaFingerprint = mediaFingerprint,
                };
            }

            var theme = themeChanged
                ? await ReadRuntimeAsync(themeName, cancellationToken).ConfigureAwait(false)
                : cachedState.Theme;
            var selectors = selectorsChanged
                ? await ReadRuntimeAsync(selectorName, cancellationToken).ConfigureAwait(false)
                : cachedState.Selectors;
            return CreateInjectionState(
                cachedState.Settings,
                mediaPath,
                theme,
                selectors,
                configFingerprint,
                themeFingerprint,
                selectorFingerprint,
                mediaFingerprint);
        }

        var settings = BackdropSettings.Load(paths.ConfigPath);
        var loadedTheme = await ReadRuntimeAsync(themeName, cancellationToken).ConfigureAwait(false);
        var loadedSelectors = await ReadRuntimeAsync(selectorName, cancellationToken).ConfigureAwait(false);
        var loadedMediaPath = settings.EffectiveBackgroundPath;
        return CreateInjectionState(
            settings,
            loadedMediaPath,
            loadedTheme,
            loadedSelectors,
            configFingerprint,
            themeFingerprint,
            selectorFingerprint,
            Fingerprint(settings.BackgroundPath));
    }

    private static InjectionState CreateInjectionState(
        BackdropSettings settings,
        string mediaPath,
        string theme,
        string selectors,
        string configFingerprint,
        string themeFingerprint,
        string selectorFingerprint,
        string mediaFingerprint)
    {
        var media = MediaDescriptor.FromPath(mediaPath);
        return new InjectionState(
            settings,
            mediaPath,
            media.Kind,
            media.MimeType,
            BuildSignature(configFingerprint, themeFingerprint, selectorFingerprint, mediaFingerprint),
            BuildCss(settings, theme, selectors),
            BackdropSentrySource.Script,
            configFingerprint,
            themeFingerprint,
            selectorFingerprint,
            mediaFingerprint,
            theme,
            selectors);
    }

    private static string BuildSignature(
        string configFingerprint,
        string themeFingerprint,
        string selectorFingerprint,
        string mediaFingerprint) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
            '|',
            configFingerprint,
            themeFingerprint,
            selectorFingerprint,
            BackdropSentrySource.Fingerprint,
            mediaFingerprint))))[..24];

    private static string TargetSetSignature(IEnumerable<Target> targets) =>
        string.Join(
            '\u001f',
            targets
                .OrderBy(target => target.Id, StringComparer.Ordinal)
                .ThenBy(target => target.WebSocketDebuggerUrl, StringComparer.Ordinal)
                .Select(target => $"{target.Id}\u001e{target.WebSocketDebuggerUrl}"));

    private static string Fingerprint(string? file)
    {
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return file + ":missing";
        var info = new FileInfo(file);
        return $"{info.FullName}:{info.Length}:{info.LastWriteTimeUtc.Ticks}";
    }

    // The stylesheets ship inside the executable. A Runtime folder next to it
    // overrides them, so a compatibility fix can be tried without a rebuild.
    private string RuntimeFile(string name) => Path.Combine(paths.RuntimeDirectory, name);

    private string RuntimeFingerprint(string name) =>
        File.Exists(RuntimeFile(name)) ? Fingerprint(RuntimeFile(name)) : "embedded:" + name;

    private async Task<string> ReadRuntimeAsync(string name, CancellationToken cancellationToken)
    {
        if (File.Exists(RuntimeFile(name)))
            return await File.ReadAllTextAsync(RuntimeFile(name), cancellationToken).ConfigureAwait(false);
        await using var stream = typeof(BackdropInjector).Assembly.GetManifestResourceStream(name)
            ?? throw new FileNotFoundException("a built-in stylesheet is missing", name);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string BuildCss(BackdropSettings settings, string theme, string selectors)
    {
        var css = theme + Environment.NewLine + selectors;
        var sidebarOpacity = EffectiveRegionOpacity(settings.SidebarStyle, settings.SidebarOpacity);
        var topOpacity = EffectiveRegionOpacity(settings.TopStyle, settings.TopOpacity);
        var replacements = new Dictionary<string, string>
        {
            ["__CANVAS__"] = settings.CanvasOpacity.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture),
            ["__SURFACE__"] = settings.SurfaceOpacity.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture),
            ["__SIDEBAR__"] = sidebarOpacity.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture),
            ["__TOP__"] = topOpacity.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture),
            ["__SIDEBAR_FILTER__"] = settings.SidebarStyle == "blended" ? "var(--codex-backdrop-glass)" : "none",
            ["__TOP_FILTER__"] = settings.TopStyle == "blended" ? "blur(3px) saturate(1.02)" : "none",
            // Solid chrome also owns the frame beside it; blended or transparent
            // chrome keeps the wallpaper underneath.
            ["__FRAME_TOP__"] = settings.TopStyle == "solid" ? "var(--codex-backdrop-surface-top, 0px)" : "0px",
            ["__FRAME_LEFT__"] = settings.SidebarStyle == "solid" ? "var(--codex-backdrop-surface-left, 0px)" : "0px",
            ["__OVERLAY__"] = settings.OverlayOpacity.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture),
            ["__BACKGROUND__"] = settings.BackgroundOpacity.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture),
            ["__BLUR__"] = settings.BlurPx.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "px",
            ["__BRIGHTNESS__"] = settings.Brightness.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture),
            ["__SATURATION__"] = settings.Saturation.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture),
            ["__FIT__"] = settings.MediaFit,
            ["__POSITION__"] = settings.MediaPosition.Replace(";", "").Replace("{", "").Replace("}", ""),
        };
        foreach (var replacement in replacements) css = css.Replace(replacement.Key, replacement.Value, StringComparison.Ordinal);
        return css;
    }

    private static double EffectiveRegionOpacity(string style, double blendedOpacity) => style switch
    {
        "transparent" => 0,
        "solid" => 1,
        _ => blendedOpacity,
    };

    private async Task<Target[]> ListTargetsAsync(
        int port,
        int generation,
        Func<CancellationToken, ValueTask<bool>> generationValidator,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TargetListTimeout);
        try
        {
            await EnsureGenerationAsync(
                generation,
                generationValidator,
                cancellationToken,
                cancellationToken).ConfigureAwait(false);
            using var response = await _http.GetAsync(
                $"http://127.0.0.1:{port}/json/list",
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumHttpResponseBytes)
                throw new InvalidDataException("codex returned an oversized renderer target list");

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var body = new MemoryStream();
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var count = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (count == 0) break;
                if (body.Length + count > MaximumHttpResponseBytes)
                    throw new InvalidDataException("codex returned an oversized renderer target list");
                body.Write(buffer, 0, count);
            }

            var targets = JsonSerializer.Deserialize<Target[]>(body.ToArray(), JsonOptions()) ?? [];
            return targets
                .Where(IsExpectedTarget)
                .Select(target => TryNormalizeTarget(target, port, out var normalized) ? normalized : null)
                .Where(target => target is not null)
                .Select(target => target!)
                .ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException("codex renderer target discovery timed out");
        }
    }

    private static bool IsExpectedTarget(Target target) =>
        target.Type == "page"
        && target.Url == "app://-/index.html"
        && !string.IsNullOrWhiteSpace(target.Id)
        && !string.IsNullOrWhiteSpace(target.WebSocketDebuggerUrl);

    private static bool TryNormalizeTarget(Target target, int port, out Target normalized)
    {
        normalized = target;
        if (!Uri.TryCreate(target.WebSocketDebuggerUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("ws" or "wss")
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.Port != port) return false;

        var path = uri.AbsolutePath.TrimEnd('/');
        const string pagePrefix = "/devtools/page/";
        if (!path.StartsWith(pagePrefix, StringComparison.Ordinal)) return false;
        var pathId = path[pagePrefix.Length..];
        if (pathId.Contains('/') || !string.Equals(Uri.UnescapeDataString(pathId), target.Id, StringComparison.Ordinal)) return false;

        var host = uri.DnsSafeHost;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            var local = new UriBuilder(uri) { Host = "127.0.0.1" };
            uri = local.Uri;
        }
        else if (!IPAddress.TryParse(host, out var address) || !IPAddress.IsLoopback(address)) return false;

        normalized = target with { WebSocketDebuggerUrl = uri.AbsoluteUri };
        return true;
    }

    private async Task<bool> ApplyTargetAsync(
        Target target,
        InjectionState state,
        int generation,
        Func<CancellationToken, ValueTask<bool>> generationValidator,
        CancellationToken cancellationToken)
    {
        using var targetTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        targetTimeout.CancelAfter(TargetTimeout);
        try
        {
            await EnsureGenerationAsync(
                generation,
                generationValidator,
                cancellationToken,
                targetTimeout.Token).ConfigureAwait(false);
            using var session = new CdpSession(
                target.WebSocketDebuggerUrl!,
                targetTimeout.Token,
                operationCancellationToken => EnsureGenerationAsync(
                    generation,
                    generationValidator,
                    cancellationToken,
                    operationCancellationToken));
            await session.ConnectAsync().ConfigureAwait(false);
            var contract = await session.CommandAsync("Runtime.evaluate", new
            {
                expression = "(()=>{const root=document.querySelector('#root'),shell=document.querySelector('main[data-app-shell-main-surface]'),documentToken=window.__codexBackdropDocumentToken??(window.__codexBackdropDocumentToken=String(Date.now())+'-'+String(Math.random())),state=window.__codexBackdrop,styles=document.querySelectorAll('#codex-backdrop-style'),layers=document.querySelectorAll('#codex-backdrop-layer'),style=styles[0],layer=layers[0],media=layer?.querySelector('.codex-backdrop-media'),sentry=window.__codexBackdropSentry?.state?.status??null;return {compatible:location.href==='app://-/index.html'&&!!document.body&&!!root&&root.isConnected&&!!shell&&shell.isConnected,starting:location.href==='app://-/index.html'&&!shell&&performance.now()<30000,documentToken,signature:state?.signature??null,intact:styles.length===1&&layers.length===1&&!!style?.isConnected&&!!layer?.isConnected&&!style.disabled&&!layer.hidden&&(!state?.expectsMedia||!!media)&&!layer?.dataset.mediaError&&!!sentry&&sentry!=='disabled'};})()",
                returnByValue = true,
            }).ConfigureAwait(false);
            var contractValue = ReadValue(contract);
            if (contractValue is not { ValueKind: JsonValueKind.Object } ||
                !contractValue.Value.TryGetProperty("compatible", out var compatible) || !compatible.GetBoolean())
            {
                // Codex renders its shell several seconds after the page loads;
                // only a page that stays without it has changed the contract.
                if (contractValue is { ValueKind: JsonValueKind.Object } &&
                    contractValue.Value.TryGetProperty("starting", out var starting) &&
                    starting.ValueKind == JsonValueKind.True)
                    throw new RendererStartingException();
                throw new InvalidOperationException("this codex update changed the ui contract; the backdrop was not applied");
            }
            if (!contractValue.Value.TryGetProperty("documentToken", out var documentTokenValue)
                || documentTokenValue.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(documentTokenValue.GetString()))
                throw new InvalidOperationException("codex did not expose a stable renderer document");
            var documentToken = documentTokenValue.GetString()!;
            if (contractValue.Value.TryGetProperty("signature", out var signature) && signature.GetString() == state.Signature &&
                contractValue.Value.TryGetProperty("intact", out var intact) && intact.GetBoolean()) return false;

            await session.CommandAsync("Page.setBypassCSP", new { enabled = true }).ConfigureAwait(false);
            var mediaUrl = await EnsureMediaAsync(session, state, documentToken, targetTimeout.Token).ConfigureAwait(false);
            // With animation off, a GIF shows only its first frame.
            var freeze = state.MimeType == "image/gif" && !state.Settings.Animate;
            var payload = JsonSerializer.Serialize(new
            {
                signature = state.Signature,
                css = state.Css,
                sentry = state.Sentry,
                kind = state.MediaKind,
                url = mediaUrl,
                mediaStamp = state.MediaFingerprint + (freeze ? "|still" : ""),
                freeze,
                playbackRate = state.Settings.PlaybackRate,
                documentToken,
            });
            var result = await session.CommandAsync("Runtime.evaluate", new
            {
                expression = InstallExpression(payload),
                returnByValue = true,
                awaitPromise = true,
                userGesture = true,
            }).ConfigureAwait(false);
            if (result.TryGetProperty("exceptionDetails", out _)) throw new InvalidOperationException("codex rejected the backdrop layer");
            var installed = ReadValue(result);
            if (installed is not { ValueKind: JsonValueKind.Object } ||
                !installed.Value.TryGetProperty("applied", out var applied) || !applied.GetBoolean() ||
                !installed.Value.TryGetProperty("documentToken", out var installedToken)
                || !string.Equals(installedToken.GetString(), documentToken, StringComparison.Ordinal))
                throw new InvalidOperationException("codex did not retain the backdrop layer");
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && targetTimeout.IsCancellationRequested)
        {
            throw new TimeoutException($"renderer target {target.Id} did not accept the backdrop before its deadline");
        }
    }

    private static async Task<string> EnsureMediaAsync(CdpSession session, InjectionState state, string documentToken, CancellationToken cancellationToken)
    {
        var encodedDocumentToken = JsonSerializer.Serialize(documentToken);
        if (state.MediaKind == "none" || string.IsNullOrWhiteSpace(state.MediaPath))
        {
            await session.CommandAsync("Runtime.evaluate", new
            {
                expression = $"(()=>{{if(window.__codexBackdropDocumentToken!=={encodedDocumentToken})throw new Error('codex replaced the renderer document');const oldUrl=window.__codexBackdropMedia?.url;if(oldUrl)URL.revokeObjectURL(oldUrl);delete window.__codexBackdropMedia;delete window.__codexBackdropTransfer;}})()",
            }).ConfigureAwait(false);
            return "";
        }

        var existing = await session.CommandAsync("Runtime.evaluate", new
        {
            expression = $"(()=>{{if(window.__codexBackdropDocumentToken!=={encodedDocumentToken})throw new Error('codex replaced the renderer document');delete window.__codexBackdropTransfer;return window.__codexBackdropMedia??null;}})()",
            returnByValue = true,
        }).ConfigureAwait(false);
        var existingValue = ReadValue(existing);
        if (existingValue is { ValueKind: JsonValueKind.Object } && existingValue.Value.TryGetProperty("stamp", out var stamp) && stamp.GetString() == state.MediaFingerprint)
            return existingValue.Value.GetProperty("url").GetString() ?? "";

        var fileInfo = new FileInfo(state.MediaPath);
        if (!fileInfo.Exists) throw new FileNotFoundException("the selected backdrop media no longer exists", state.MediaPath);
        if (fileInfo.Length > MaximumMediaBytes) throw new InvalidDataException("the selected backdrop media is too large");

        await session.CommandAsync("Runtime.evaluate", new
        {
            expression = $"(()=>{{if(window.__codexBackdropDocumentToken!=={encodedDocumentToken})throw new Error('codex replaced the renderer document');window.__codexBackdropTransfer={{stamp:{JsonSerializer.Serialize(state.MediaFingerprint)},mime:{JsonSerializer.Serialize(state.MimeType)},chunks:[]}};}})()",
        }).ConfigureAwait(false);

        await using var stream = File.OpenRead(state.MediaPath);
        var buffer = new byte[256 * 1024];
        long totalBytes = 0;
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            totalBytes += count;
            if (totalBytes > MaximumMediaBytes) throw new InvalidDataException("the selected backdrop media is too large");
            var base64 = Convert.ToBase64String(buffer, 0, count);
            await session.CommandAsync("Runtime.evaluate", new
            {
                expression = $"(()=>{{if(window.__codexBackdropDocumentToken!=={encodedDocumentToken})throw new Error('codex replaced the renderer document');const b=atob({JsonSerializer.Serialize(base64)});window.__codexBackdropTransfer.chunks.push(Uint8Array.from(b,c=>c.charCodeAt(0)));}})()",
            }).ConfigureAwait(false);
        }

        var finished = await session.CommandAsync("Runtime.evaluate", new
        {
            expression = $"(()=>{{if(window.__codexBackdropDocumentToken!=={encodedDocumentToken})throw new Error('codex replaced the renderer document');const t=window.__codexBackdropTransfer;if(!t)throw new Error('codex lost the backdrop transfer');const b=new Blob(t.chunks,{{type:t.mime}}),oldUrl=window.__codexBackdropMedia?.url;window.__codexBackdropMedia={{stamp:t.stamp,url:URL.createObjectURL(b),bytes:b.size,previousUrl:oldUrl}};delete window.__codexBackdropTransfer;return window.__codexBackdropMedia;}})()",
            returnByValue = true,
        }).ConfigureAwait(false);
        return ReadValue(finished)?.GetProperty("url").GetString() ?? "";
    }

    private static JsonElement? ReadValue(JsonElement commandResult)
    {
        if (commandResult.TryGetProperty("exceptionDetails", out var exceptionDetails))
        {
            var text = exceptionDetails.TryGetProperty("text", out var exceptionText) ? exceptionText.GetString() : null;
            throw new InvalidOperationException("codex runtime evaluation failed" + (string.IsNullOrWhiteSpace(text) ? "" : $": {text}"));
        }
        if (!commandResult.TryGetProperty("result", out var remote) || !remote.TryGetProperty("value", out var value)) return null;
        return value;
    }

    private static string InstallExpression(string payload) => """
        (payload => {
          if(location.href!=='app://-/index.html'||window.__codexBackdropDocumentToken!==payload.documentToken)throw new Error('codex replaced the renderer document');
          window.__codexBackdropSentry?.disconnect?.();
          const styles=[...document.querySelectorAll('#codex-backdrop-style')],style=styles.shift()??document.createElement('style');for(const extra of styles)extra.remove();style.id='codex-backdrop-style';style.disabled=false;style.textContent=payload.css;if(!style.isConnected)(document.head||document.documentElement).append(style);
          const layers=[...document.querySelectorAll('#codex-backdrop-layer')],layer=layers.shift()??document.createElement('div');for(const extra of layers)extra.remove();layer.id='codex-backdrop-layer';layer.hidden=false;layer.setAttribute('aria-hidden','true');if(!layer.isConnected)(document.body||document.documentElement).prepend(layer);
          let media=layer.querySelector(':scope > .codex-backdrop-media');for(const extra of [...layer.querySelectorAll(':scope > .codex-backdrop-media')].slice(1))extra.remove();
          const release=node=>{if(node?.dataset.codexBackdropStill)URL.revokeObjectURL(node.dataset.codexBackdropStill);};
          if(payload.kind==='none'||!payload.url){release(media);media?.remove();media=null;}else{const expected=payload.kind==='video'?'VIDEO':'IMG';if(!media||media.tagName!==expected||media.dataset.codexBackdropStamp!==payload.mediaStamp||layer.dataset.mediaError){const replacement=document.createElement(payload.kind==='video'?'video':'img');replacement.className='codex-backdrop-media';replacement.dataset.codexBackdropStamp=payload.mediaStamp;if(payload.freeze)replacement.addEventListener('load',()=>{const canvas=document.createElement('canvas');canvas.width=replacement.naturalWidth;canvas.height=replacement.naturalHeight;canvas.getContext('2d').drawImage(replacement,0,0);canvas.toBlob(blob=>{if(!blob||!replacement.isConnected)return;replacement.dataset.codexBackdropStill=URL.createObjectURL(blob);replacement.src=replacement.dataset.codexBackdropStill;},'image/png');},{once:true});replacement.src=payload.url;replacement.addEventListener('error',()=>layer.dataset.mediaError='load');release(media);if(media)media.replaceWith(replacement);else layer.prepend(replacement);media=replacement;}if(payload.kind==='video'){media.autoplay=true;media.loop=true;media.muted=true;media.defaultMuted=true;media.playsInline=true;media.playbackRate=payload.playbackRate;media.play().catch(()=>layer.dataset.mediaError='autoplay');}}
          let overlay=layer.querySelector(':scope > .codex-backdrop-overlay');if(!overlay){overlay=document.createElement('div');overlay.className='codex-backdrop-overlay';layer.append(overlay);}delete layer.dataset.mediaError;
          const staleUrl=window.__codexBackdropMedia?.previousUrl;if(staleUrl&&staleUrl!==payload.url)URL.revokeObjectURL(staleUrl);if(window.__codexBackdropMedia)delete window.__codexBackdropMedia.previousUrl;
          const installSentry=(0,eval)(payload.sentry);if(typeof installSentry!=='function')throw new Error('invalid backdrop sentry');
          window.__codexBackdropSentry=installSentry({signature:payload.signature,style,layer});
          window.__codexBackdrop={signature:payload.signature,documentToken:payload.documentToken,expectsMedia:payload.kind!=='none'&&!!payload.url,appliedAt:Date.now()};return {applied:style.isConnected&&layer.isConnected,documentToken:window.__codexBackdropDocumentToken,kind:payload.kind,sentry:window.__codexBackdropSentry?.state?.status??null};
        })(__PAYLOAD__)
        """.Replace("__PAYLOAD__", payload, StringComparison.Ordinal);

    private Task LogAsync(string message)
    {
        BackdropLog.Append(paths.LogPath, message);
        return Task.CompletedTask;
    }

    private static JsonSerializerOptions JsonOptions() => new() { PropertyNameCaseInsensitive = true };

    private sealed record StopSnapshot(
        int Generation,
        CancellationTokenSource? Cancellation,
        Task? Worker,
        TaskCompletionSource? FirstHealthy,
        long Deadline);

    private sealed record InjectionState(
        BackdropSettings Settings,
        string MediaPath,
        string MediaKind,
        string MimeType,
        string Signature,
        string Css,
        string Sentry,
        string ConfigFingerprint,
        string ThemeFingerprint,
        string SelectorFingerprint,
        string MediaFingerprint,
        string Theme,
        string Selectors);
    private sealed record Target(string? Id, string? Type, string? Url, string? WebSocketDebuggerUrl);

    private sealed class GenerationValidationException : InvalidOperationException
    {
        public GenerationValidationException(string message, Exception? innerException = null)
            : base(message, innerException) { }
    }

    private sealed class RendererStartingException() : InvalidOperationException("codex is still starting");

    private sealed record MediaDescriptor(string Kind, string MimeType)
    {
        public static MediaDescriptor FromPath(string? file)
        {
            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return new("none", "application/octet-stream");
            try
            {
                if (new FileInfo(file).Length > MaximumMediaBytes) return new("none", "application/octet-stream");
            }
            catch
            {
                return new("none", "application/octet-stream");
            }

            return Path.GetExtension(file).ToLowerInvariant() switch
            {
                ".png" => new("image", "image/png"),
                ".jpg" or ".jpeg" => new("image", "image/jpeg"),
                ".webp" => new("image", "image/webp"),
                ".gif" => new("image", "image/gif"),
                ".mp4" => new("video", "video/mp4"),
                ".webm" => new("video", "video/webm"),
                _ => new("none", "application/octet-stream"),
            };
        }
    }

    private sealed class CdpSession(
        string url,
        CancellationToken cancellationToken,
        Func<CancellationToken, ValueTask> generationValidator) : IDisposable
    {
        private readonly ClientWebSocket _socket = new();
        private int _sequence;

        public async Task ConnectAsync()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);
            try
            {
                await generationValidator(timeout.Token).ConfigureAwait(false);
                await _socket.ConnectAsync(new Uri(url), timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
            {
                throw new TimeoutException("codex renderer connection timed out");
            }
        }

        public async Task<JsonElement> CommandAsync(string method, object parameters)
        {
            var id = Interlocked.Increment(ref _sequence);
            var request = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters });
            if (request.Length > MaximumWebSocketMessageBytes)
                throw new InvalidDataException("codex renderer command is oversized");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CommandTimeout);
            try
            {
                if (_socket.State != WebSocketState.Open) throw new IOException("codex closed the renderer connection");
                await generationValidator(timeout.Token).ConfigureAwait(false);
                await _socket.SendAsync(request, WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);

                while (true)
                {
                    using var message = new MemoryStream();
                    var buffer = new byte[MaximumWebSocketFrameBytes];
                    WebSocketReceiveResult received;
                    do
                    {
                        received = await _socket.ReceiveAsync(buffer, timeout.Token).ConfigureAwait(false);
                        if (received.MessageType == WebSocketMessageType.Close)
                            throw new IOException("codex closed the renderer connection");
                        if (received.MessageType != WebSocketMessageType.Text)
                            throw new InvalidDataException("codex returned a non-text renderer message");
                        if (received.Count > MaximumWebSocketFrameBytes || message.Length + received.Count > MaximumWebSocketMessageBytes)
                            throw new InvalidDataException("codex returned an oversized renderer message");
                        message.Write(buffer, 0, received.Count);
                    } while (!received.EndOfMessage);

                    using var document = JsonDocument.Parse(message.ToArray());
                    var root = document.RootElement;
                    if (root.TryGetProperty("method", out var eventMethod))
                    {
                        if (eventMethod.GetString() == "Inspector.detached")
                        {
                            var reason = root.TryGetProperty("params", out var eventParams)
                                && eventParams.TryGetProperty("reason", out var eventReason)
                                ? eventReason.GetString()
                                : null;
                            throw new IOException("codex detached the renderer connection" + (string.IsNullOrWhiteSpace(reason) ? "" : $": {reason}"));
                        }
                        continue;
                    }
                    if (!root.TryGetProperty("id", out var responseId) || !responseId.TryGetInt32(out var responseIdValue) || responseIdValue != id) continue;
                    if (root.TryGetProperty("error", out var error))
                    {
                        var errorMessage = error.TryGetProperty("message", out var messageValue) ? messageValue.GetString() : null;
                        throw new InvalidOperationException(errorMessage ?? "codex rejected the renderer command");
                    }
                    if (!root.TryGetProperty("result", out var result)) throw new InvalidDataException("codex returned a renderer response without a result");
                    return result.Clone();
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
            {
                throw new TimeoutException($"codex renderer command {method} timed out");
            }
        }

        public void Dispose()
        {
            try { _socket.Dispose(); } catch { }
        }
    }
}
