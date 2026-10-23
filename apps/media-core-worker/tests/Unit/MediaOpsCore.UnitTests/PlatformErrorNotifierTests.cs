using MediaOpsCore.Workers.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class PlatformErrorNotifierTests : IDisposable
{
    private readonly string tempDir = Path.Combine(Path.GetTempPath(), $"platform-err-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(tempDir))
            Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public void TryMapOptedInUser_keeps_enabled_users_and_their_contact_fields()
    {
        var users = new[]
        {
            """{ "notifyPlatformErrors": true,  "phone": "+573001112233", "email": "a@x.co" }""",
            """{ "notifyPlatformErrors": true,  "phone": "", "email": "b@x.co" }""",
            """{ "notifyPlatformErrors": false, "phone": "+573004445566", "email": "c@x.co" }""",
            """{ "notifyPlatformErrors": true }""",
            """{ "phone": "+573007778899" }""",
        }
        .Select(json => MongoDocumentMapper.TryMapOptedInUser(MongoDB.Bson.BsonDocument.Parse(json)))
        .OfType<MongoDocumentMapper.OptedInUser>()
        .ToArray();

        Assert.Equal(
            [new MongoDocumentMapper.OptedInUser("+573001112233", "a@x.co"), new MongoDocumentMapper.OptedInUser(null, "b@x.co")],
            users);
    }

    // A sidecar that does not answer makes HttpClient throw TaskCanceledException (its own timeout): that is a
    // disconnected WhatsApp, not a reason for the whole worker — and the capture with it — to stop.
    private sealed class TimingOutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.");
    }

    [Fact]
    public async Task Watchdog_reads_a_sidecar_that_times_out_as_disconnected_instead_of_throwing()
    {
        var sut = new WhatsAppConnectionWatchdog(
            new OperationsWorkerOptions(), new HttpClient(new TimingOutHandler()), new CountingNotifier(), NullLogger<WhatsAppConnectionWatchdog>.Instance);

        Assert.False(await sut.IsConnectedAsync(CancellationToken.None));
    }

    // A sidecar that accepts the connection and never answers: only the watchdog's own limit ends the wait.
    private sealed class SilentHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    [Fact]
    public async Task Watchdog_gives_up_on_a_silent_sidecar_after_its_own_short_limit_not_the_clients_100_seconds()
    {
        var sut = new WhatsAppConnectionWatchdog(
            new OperationsWorkerOptions(), new HttpClient(new SilentHandler()), new CountingNotifier(),
            NullLogger<WhatsAppConnectionWatchdog>.Instance, statusTimeout: TimeSpan.FromMilliseconds(200));
        var started = DateTime.UtcNow;

        var connected = await sut.IsConnectedAsync(CancellationToken.None);

        Assert.False(connected);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Watchdog_still_stops_when_the_worker_is_stopping()
    {
        var sut = new WhatsAppConnectionWatchdog(
            new OperationsWorkerOptions(), new HttpClient(new TimingOutHandler()), new CountingNotifier(), NullLogger<WhatsAppConnectionWatchdog>.Instance);
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.IsConnectedAsync(stopping.Token));
    }

    [Fact]
    public void Watchdog_alerts_once_per_outage_after_the_grace_period()
    {
        var sut = new WhatsAppConnectionWatchdog(new OperationsWorkerOptions(), new HttpClient(), new CountingNotifier(), NullLogger<WhatsAppConnectionWatchdog>.Instance);
        var t0 = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

        Assert.False(sut.Observe(false, t0));
        Assert.False(sut.Observe(false, t0.AddMinutes(2)));
        Assert.True(sut.Observe(false, t0.AddMinutes(3)));
        Assert.False(sut.Observe(false, t0.AddMinutes(10)));

        Assert.False(sut.Observe(true, t0.AddMinutes(11)));
        Assert.False(sut.Observe(false, t0.AddMinutes(12)));
        Assert.True(sut.Observe(false, t0.AddMinutes(15)));
    }

    [Fact]
    public void WriteAlert_notifies_once_while_the_flag_stays_and_again_after_it_is_cleared()
    {
        var notifier = new CountingNotifier();
        var sut = new YouTubeCookiesAlertService(
            new OperationsWorkerOptions { YoutubeCookiesAlertFilePath = Path.Combine(tempDir, "flag") },
            NullLogger<YouTubeCookiesAlertService>.Instance,
            notifier);

        sut.WriteAlert("rcn_tv", "expired");
        sut.WriteAlert("caracol_tv", "expired");
        Assert.Equal(1, notifier.Calls);
        Assert.Contains("cookies", notifier.LastMessage, StringComparison.OrdinalIgnoreCase);

        sut.ClearAlert();
        sut.WriteAlert("rcn_tv", "expired again");
        Assert.Equal(2, notifier.Calls);
    }

    private sealed class CountingNotifier : IPlatformErrorNotifier
    {
        public int Calls { get; private set; }
        public string LastMessage { get; private set; } = string.Empty;

        public Task NotifyAsync(string message, string subject, bool viaWhatsApp = true, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastMessage = message;
            return Task.CompletedTask;
        }
    }
}
