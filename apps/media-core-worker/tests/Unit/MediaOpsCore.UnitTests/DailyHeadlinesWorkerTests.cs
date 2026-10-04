using MediaOpsCore.Modules.Alerting.Application;
using MediaOpsCore.Modules.Alerting.Infrastructure;
using MediaOpsCore.Workers.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaOpsCore.UnitTests;

public sealed class DailyHeadlinesWorkerTests
{
    private static readonly TimeSpan Bogota = TimeSpan.FromHours(-5);

    private static (DailyHeadlinesWorker Worker, FakeDigest Digest, FakeLog Log) Build(DateTimeOffset now)
    {
        var digest = new FakeDigest();
        var log = new FakeLog();
        var worker = new DailyHeadlinesWorker(digest, log, new TimeOnly(7, 0), NullLogger<DailyHeadlinesWorker>.Instance, () => now);
        return (worker, digest, log);
    }

    [Fact]
    public async Task RunOnceAsync_does_nothing_before_the_send_time()
    {
        var (worker, digest, log) = Build(new DateTimeOffset(2026, 10, 3, 6, 59, 0, Bogota));

        await worker.RunOnceAsync(CancellationToken.None);

        Assert.Empty(digest.Days);
        Assert.Empty(log.Sent);
    }

    [Fact]
    public async Task RunOnceAsync_sends_the_digest_once_and_records_the_day()
    {
        var (worker, digest, log) = Build(new DateTimeOffset(2026, 10, 3, 7, 5, 0, Bogota));

        await worker.RunOnceAsync(CancellationToken.None);
        await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal([new DateOnly(2026, 10, 3)], digest.Days);
        Assert.Equal([new DateOnly(2026, 10, 3)], log.Sent);
    }

    [Fact]
    public async Task RunOnceAsync_skips_a_day_that_was_already_sent_before_a_restart()
    {
        var (worker, digest, log) = Build(new DateTimeOffset(2026, 10, 3, 9, 0, 0, Bogota));
        log.Sent.Add(new DateOnly(2026, 10, 3));

        await worker.RunOnceAsync(CancellationToken.None);

        Assert.Empty(digest.Days);
    }

    private sealed class FakeDigest : IDailyHeadlinesDigestUseCase
    {
        public List<DateOnly> Days { get; } = [];

        public Task<int> ExecuteAsync(DateOnly bogotaDay, CancellationToken cancellationToken = default)
        {
            Days.Add(bogotaDay);
            return Task.FromResult(1);
        }
    }

    private sealed class FakeLog : IDailyDigestLog
    {
        public List<DateOnly> Sent { get; } = [];

        public Task<bool> WasSentAsync(DateOnly day, CancellationToken cancellationToken = default) => Task.FromResult(Sent.Contains(day));

        public Task MarkSentAsync(DateOnly day, int clientsNotified, CancellationToken cancellationToken = default)
        {
            Sent.Add(day);
            return Task.CompletedTask;
        }
    }
}

public sealed class RoutingMessageNotifierTests
{
    private sealed class Recording : IMessageNotifier
    {
        public List<string> Received { get; } = [];

        public Task<IReadOnlyList<string>> SendAsync(string text, IReadOnlyList<string> recipients, string? subject = null, CancellationToken cancellationToken = default)
        {
            Received.AddRange(recipients);
            return Task.FromResult(recipients);
        }
    }

    [Fact]
    public async Task SendAsync_routes_emails_to_the_email_channel_and_the_rest_to_whatsapp()
    {
        var whatsApp = new Recording();
        var email = new Recording();
        var sut = new RoutingMessageNotifier(whatsApp, email, NullLogger<RoutingMessageNotifier>.Instance);

        await sut.SendAsync("texto", ["573001112233", "ana@entidad.gov.co"]);

        Assert.Equal(["573001112233"], whatsApp.Received);
        Assert.Equal(["ana@entidad.gov.co"], email.Received);
    }

    [Fact]
    public async Task SendAsync_never_texts_an_email_address_when_smtp_is_not_configured()
    {
        var whatsApp = new Recording();
        var sut = new RoutingMessageNotifier(whatsApp, null, NullLogger<RoutingMessageNotifier>.Instance);

        var reached = await sut.SendAsync("texto", ["573001112233", "ana@entidad.gov.co"]);

        Assert.Equal(["573001112233"], whatsApp.Received);
        Assert.Equal(["573001112233"], reached);
    }
}
