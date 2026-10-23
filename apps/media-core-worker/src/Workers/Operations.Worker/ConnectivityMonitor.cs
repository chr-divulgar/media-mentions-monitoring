using MediaOpsCore.Modules.Capture.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Whether the machine can reach the internet right now, and
/// the moments that changes. The worker waits on it at startup and pauses its recovery work while
/// it is offline.
/// </summary>
public interface IConnectivityMonitor
{
    bool IsOnline { get; }

    /// <summary>Completes as soon as there is a connection (immediately if there already is one).</summary>
    Task WaitUntilOnlineAsync(CancellationToken cancellationToken = default);

    /// <summary>Raised when the connection comes back after having been lost (not at the first connection).</summary>
    event Action? CameOnline;
}

/// <summary>
/// Probes the connection every minute. Starting without internet used to leave the worker on the
/// stale local source catalogue, marking every source excluded, until somebody restarted it; with
/// this, the worker waits for the connection and carries on by itself when it returns.
///
/// Offline is the initial state until the first probe succeeds, so nothing that checks
/// <see cref="IsOnline"/> acts on the network before it has been seen working.
/// </summary>
public sealed class ConnectivityMonitor : BackgroundService, IConnectivityMonitor
{
    private readonly bool enabled;
    private readonly TimeSpan interval;
    private readonly Func<CancellationToken, Task<bool>> probe;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly ISourceIncidentRecorder incidents;
    private readonly ILogger<ConnectivityMonitor> logger;
    private readonly object gate = new();
    private TaskCompletionSource onlineSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool? lastKnownOnline;
    private bool hasBeenOnline;
    private volatile bool isOnline;

    public ConnectivityMonitor(
        OperationsWorkerOptions options,
        HttpClient httpClient,
        ISourceIncidentRecorder incidents,
        ILogger<ConnectivityMonitor> logger)
        : this(
            options.ConnectivityCheckEnabled,
            TimeSpan.FromSeconds(Math.Max(1, options.ConnectivityCheckIntervalSeconds)),
            cancellationToken => ConnectivityProbe.IsOnlineAsync(
                httpClient,
                ConnectivityProbe.ParseUrls(options.ConnectivityCheckUrls),
                null,
                TimeSpan.FromSeconds(Math.Max(1, options.ConnectivityCheckTimeoutSeconds)),
                cancellationToken),
            incidents,
            logger)
    {
    }

    // Test seam: the probe and the wait between probes are injected.
    internal ConnectivityMonitor(
        bool enabled,
        TimeSpan interval,
        Func<CancellationToken, Task<bool>> probe,
        ISourceIncidentRecorder incidents,
        ILogger<ConnectivityMonitor> logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.enabled = enabled;
        this.interval = interval;
        this.probe = probe;
        this.incidents = incidents;
        this.logger = logger;
        this.delay = delay ?? Task.Delay;
        if (!enabled)
        {
            // The check is switched off: behave as always connected, like before this existed.
            isOnline = true;
            hasBeenOnline = true;
            onlineSignal.TrySetResult();
        }
    }

    public bool IsOnline => isOnline;

    public event Action? CameOnline;

    public Task WaitUntilOnlineAsync(CancellationToken cancellationToken = default)
    {
        Task signal;
        lock (gate)
        {
            signal = onlineSignal.Task;
        }

        return signal.WaitAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!enabled)
        {
            logger.LogInformation("[ConnectivityMonitor] Connectivity check is disabled.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckOnceAsync(stoppingToken).ConfigureAwait(false);
                await delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "[ConnectivityMonitor] Connectivity check failed unexpectedly.");
            }
        }
    }

    internal async Task CheckOnceAsync(CancellationToken cancellationToken)
    {
        bool online;
        try
        {
            online = await probe(cancellationToken).ConfigureAwait(false);
        }
        // A probe that times out throws TaskCanceledException: it is "offline", not an error to retry at once.
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(exception, "[ConnectivityMonitor] Probe threw; counting it as offline.");
            online = false;
        }

        Apply(online);
    }

    private void Apply(bool online)
    {
        var previous = lastKnownOnline;
        lastKnownOnline = online;
        var cameBack = false;

        lock (gate)
        {
            isOnline = online;
            if (online)
            {
                cameBack = previous == false && hasBeenOnline;
                hasBeenOnline = true;
                onlineSignal.TrySetResult();
            }
            else if (onlineSignal.Task.IsCompleted)
            {
                onlineSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        if (!online)
        {
            if (previous != false)
            {
                // Recorded once per outage, not on every retry.
                incidents.Record(
                    IncidentKinds.WorkerSourceId,
                    IncidentKinds.ConnectionLost,
                    previous is null ? "Sin conexión a internet al arrancar; se espera y se reintenta cada minuto" : "Se perdió la conexión a internet");
            }

            logger.LogWarning("[ConnectivityMonitor] No internet connection; retrying in {Seconds:0} s.", interval.TotalSeconds);
            return;
        }

        if (previous == false)
        {
            incidents.Record(IncidentKinds.WorkerSourceId, IncidentKinds.ConnectionRestored, "Volvió la conexión a internet");
            logger.LogInformation("[ConnectivityMonitor] Internet connection is back.");
        }

        if (cameBack)
        {
            CameOnline?.Invoke();
        }
    }
}

/// <summary>The probe behind <see cref="ConnectivityMonitor"/>: a short HTTP request to a few well-known endpoints.</summary>
public static class ConnectivityProbe
{
    internal static readonly string[] DefaultUrls =
    [
        "https://www.gstatic.com/generate_204",
        "https://clients3.google.com/generate_204",
    ];

    // Comma-separated, like the other list options; empty falls back to the defaults.
    internal static IReadOnlyList<string> ParseUrls(string? configured)
    {
        var urls = (configured ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            .ToArray();
        return urls.Length > 0 ? urls : DefaultUrls;
    }

    /// <summary>
    /// Online when at least one of <paramref name="urls"/> answers and, when given, so does
    /// <paramref name="requiredUrl"/>. Any HTTP answer counts: what matters is that the connection
    /// was made, not the status code.
    /// </summary>
    public static async Task<bool> IsOnlineAsync(
        HttpClient httpClient,
        IReadOnlyList<string> urls,
        string? requiredUrl,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var anyAnswered = false;
        foreach (var url in urls)
        {
            if (await AnswersAsync(httpClient, url, timeout, cancellationToken).ConfigureAwait(false))
            {
                anyAnswered = true;
                break;
            }
        }

        if (!anyAnswered)
        {
            return false;
        }

        return requiredUrl is null || await AnswersAsync(httpClient, requiredUrl, timeout, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> AnswersAsync(HttpClient httpClient, string url, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
