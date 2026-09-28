using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CodexBackdrop;

var root = Path.Combine(
    Path.GetTempPath(),
    $"codex-backdrop-injector-fence-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);

try
{
    await TestValidatorStopsBeforeDiscoveryAsync(root);
    await TestValidatorStopsBeforeTheNextPollAsync(root);
    await TestValidatorIsBoundedAsync(root);
    await TestStopDoesNotLoseAStartPublicationAsync(root);
    Console.WriteLine("injector generation fence: all checks passed");
}
finally
{
    try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    catch { }
}

static async Task TestValidatorStopsBeforeDiscoveryAsync(string root)
{
    var paths = CreatePaths(root, "before-discovery");
    var calls = 0;
    using var injector = new BackdropInjector(paths);

    var error = await ExpectFailureAsync(() => injector.StartAsync(
        9223,
        TimeSpan.FromSeconds(5),
        _ => RejectGeneration(ref calls)));

    Expect(error.Message.Contains("no longer the recorded process", StringComparison.Ordinal),
        "validator failure is surfaced as a generation failure");
    Expect(calls == 1, "failed validation prevents renderer discovery");
    Expect(!injector.IsHealthy, "a failed validator leaves the injector unhealthy");
}

static async Task TestValidatorStopsBeforeTheNextPollAsync(string root)
{
    var paths = CreatePaths(root, "before-next-poll");
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var requests = 0;
    using var serverCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var server = ServeEmptyTargetListAsync(listener, () => Interlocked.Increment(ref requests), serverCancellation.Token);

    var calls = 0;
    using var injector = new BackdropInjector(paths);
    var error = await ExpectFailureAsync(() => injector.StartAsync(
        port,
        TimeSpan.FromSeconds(5),
        _ => new ValueTask<bool>(Interlocked.Increment(ref calls) < 3)));

    Expect(error.Message.Contains("no longer the recorded process", StringComparison.Ordinal),
        "validator failure on a later poll is terminal");
    Expect(requests == 1, "the worker does not poll a reused listener after validation fails");
    Expect(calls >= 3, "the validator runs again before the next poll");

    serverCancellation.Cancel();
    try { await server; } catch (OperationCanceledException) { }
}

static async Task TestValidatorIsBoundedAsync(string root)
{
    var paths = CreatePaths(root, "bounded-validator");
    using var injector = new BackdropInjector(paths);
    var stopwatch = Stopwatch.StartNew();

    var error = await ExpectFailureAsync(() => injector.StartAsync(
        9223,
        TimeSpan.FromSeconds(5),
        async cancellationToken =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            return true;
        }));

    stopwatch.Stop();
    Expect(error.Message.Contains("validation timed out", StringComparison.Ordinal),
        "a stuck validator fails closed with a bounded error");
    Expect(stopwatch.Elapsed < TimeSpan.FromSeconds(5), "validator execution is bounded");
}

static async Task TestStopDoesNotLoseAStartPublicationAsync(string root)
{
    var paths = CreatePaths(root, "stop-during-start-publication");
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    using var serverCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var server = ServeEmptyTargetListAsync(listener, () => { }, serverCancellation.Token);

    using var injector = new BackdropInjector(paths);
    var validatorEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseValidator = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Task? stopTask = null;
    var validatorCalls = 0;

    try
    {
        var startTask = injector.StartAsync(
            port,
            TimeSpan.FromSeconds(5),
            async cancellationToken =>
            {
                Interlocked.Increment(ref validatorCalls);
                stopTask = injector.StopAsync();
                validatorEntered.TrySetResult();
                await releaseValidator.Task.WaitAsync(cancellationToken);
                return true;
            });

        await validatorEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Expect(stopTask is not null, "stop request was created during generation publication");
        releaseValidator.TrySetResult();

        await stopTask!.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromMilliseconds(900));
        Expect(Volatile.Read(ref validatorCalls) == 1,
            "a stop request created during start publication stops the published worker");

        var error = await ExpectFailureAsync(() => startTask);
        Expect(error is TimeoutException, "stopping the published worker cancels the pending start");
    }
    finally
    {
        serverCancellation.Cancel();
        try { await server; } catch (OperationCanceledException) { }
    }
}

static AppPaths CreatePaths(string root, string name)
{
    var directory = Path.Combine(root, name);
    var runtime = Path.Combine(directory, "Runtime");
    Directory.CreateDirectory(runtime);
    File.WriteAllText(Path.Combine(runtime, "theme.css"), "body { color: white; }");
    File.WriteAllText(Path.Combine(runtime, "codex-selectors.css"), "");
    return new AppPaths(
        directory,
        Path.Combine(directory, "config.json"),
        Path.Combine(directory, "injector.log"),
        Path.Combine(directory, "supervisor-state.json"),
        runtime);
}

static ValueTask<bool> RejectGeneration(ref int calls)
{
    Interlocked.Increment(ref calls);
    return new ValueTask<bool>(false);
}

static async Task ServeEmptyTargetListAsync(
    TcpListener listener,
    Action onRequest,
    CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        onRequest();
        await using var stream = client.GetStream();
        var buffer = new byte[4096];
        var received = 0;
        while (received < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(received), cancellationToken);
            if (count == 0) break;
            received += count;
            if (Encoding.UTF8.GetString(buffer, 0, received).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
        }

        var body = "[]";
        var response = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
        var bytes = Encoding.ASCII.GetBytes(response);
        await stream.WriteAsync(bytes, cancellationToken);
    }
}

static async Task<Exception> ExpectFailureAsync(Func<Task> operation)
{
    try
    {
        await operation();
    }
    catch (Exception error)
    {
        return error;
    }

    throw new InvalidOperationException("expected the injector operation to fail");
}

static void Expect(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("failed: " + message);
}
