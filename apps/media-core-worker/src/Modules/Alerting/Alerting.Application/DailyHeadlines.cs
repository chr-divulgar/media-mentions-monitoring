using System.Globalization;
using System.Text;
using MediaOpsCore.Modules.Alerting.Domain;

namespace MediaOpsCore.Modules.Alerting.Application;

// Sends free-form text (not an Alert) to a recipients list — the digest has no single alert to
// format. Same best-effort contract as IAlertNotifier: returns who was actually reached.
public interface IMessageNotifier
{
    Task<IReadOnlyList<string>> SendAsync(string text, IReadOnlyList<string> recipients, string? subject = null, CancellationToken cancellationToken = default);
}

// Alerts of one media inside a UTC window — what a digest summarises.
public interface IDailyHeadlinesSource
{
    Task<IReadOnlyList<Alert>> GetAlertsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, string media, CancellationToken cancellationToken = default);
}

// Remembers which days already got their digest, so a restart at 07:05 does not send it twice.
public interface IDailyDigestLog
{
    Task<bool> WasSentAsync(DateOnly day, CancellationToken cancellationToken = default);

    Task MarkSentAsync(DateOnly day, int clientsNotified, CancellationToken cancellationToken = default);
}

public static class DailyHeadlinesSchedule
{
    private static readonly TimeSpan BogotaOffset = TimeSpan.FromHours(-5);

    // "Today" and the send time are both Bogotá wall-clock, wherever the server runs.
    public static DateOnly BogotaDay(DateTimeOffset now) => DateOnly.FromDateTime(now.ToOffset(BogotaOffset).DateTime);

    public static bool IsDue(DateTimeOffset now, TimeOnly sendTime, bool alreadySentToday) =>
        !alreadySentToday && TimeOnly.FromDateTime(now.ToOffset(BogotaOffset).DateTime) >= sendTime;
}

public interface IDailyHeadlinesDigestUseCase
{
    // Returns how many clients received a digest.
    Task<int> ExecuteAsync(DateOnly bogotaDay, CancellationToken cancellationToken = default);
}

// RQ-035 (FIC-28, FIC-29): a daily message of the day's headlines per client plus a "top del día".
// Only web mentions exist as headlines today (press and magazines arrive with F3), so the digest is
// built from the worker's internet alerts. Assumption (AMB-022): with no impact score yet (RQ-027),
// the top ranks by alert level, then by recency; swap the ranking once the score exists.
public sealed class DailyHeadlinesDigestUseCase : IDailyHeadlinesDigestUseCase
{
    private const string Media = "internet";
    private const int TopCount = 5;
    private const int MaxListed = 30;
    private static readonly TimeSpan BogotaOffset = TimeSpan.FromHours(-5);

    private readonly IClientConfigRepository clientConfigRepository;
    private readonly IDailyHeadlinesSource source;
    private readonly IMessageNotifier notifier;

    public DailyHeadlinesDigestUseCase(
        IClientConfigRepository clientConfigRepository,
        IDailyHeadlinesSource source,
        IMessageNotifier notifier)
    {
        this.clientConfigRepository = clientConfigRepository;
        this.source = source;
        this.notifier = notifier;
    }

    public async Task<int> ExecuteAsync(DateOnly bogotaDay, CancellationToken cancellationToken = default)
    {
        var from = new DateTimeOffset(bogotaDay.ToDateTime(TimeOnly.MinValue), BogotaOffset);
        // An alert waiting for manual review never had its date confirmed: it may not be from this day.
        var alerts = (await source.GetAlertsAsync(from, from.AddDays(1), Media, cancellationToken).ConfigureAwait(false))
            .Where(alert => alert.ReviewReason is null)
            .ToArray();
        var clients = await clientConfigRepository.GetClientsAsync(cancellationToken).ConfigureAwait(false);

        var notified = 0;
        foreach (var client in clients)
        {
            var recipients = RecipientsFor(client);
            var headlines = alerts
                .Where(alert => string.Equals(alert.ClientName, client.Name, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (recipients.Count == 0 || headlines.Length == 0)
            {
                continue;
            }

            var text = BuildMessage(client.Name, bogotaDay, headlines);
            var reached = await notifier
                .SendAsync(text, recipients, $"Titulares del día — {client.Name}", cancellationToken)
                .ConfigureAwait(false);

            if (reached.Count > 0)
            {
                notified++;
            }
        }

        return notified;
    }

    private static IReadOnlyList<string> RecipientsFor(ClientKeywordConfig client)
    {
        var phones = client.AlertRecipientsByMedia.TryGetValue(Media, out var numbers) ? numbers : [];
        var emails = client.AlertEmailsByMedia is not null && client.AlertEmailsByMedia.TryGetValue(Media, out var addresses) ? addresses : [];
        return phones.Concat(emails).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string BuildMessage(string clientName, DateOnly day, IReadOnlyList<Alert> headlines)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"*Titulares del día — {clientName} — {day:dd/MM/yyyy}*\n");

        var top = headlines.OrderByDescending(alert => alert.Level).ThenByDescending(alert => alert.EndTime).Take(TopCount).ToArray();
        text.Append("\n*Top del día*\n");
        for (var i = 0; i < top.Length; i++)
        {
            var level = top[i].Level == AlertLevel.Informative ? string.Empty : $"[{top[i].Level.ToLabel()}] ";
            text.Append(CultureInfo.InvariantCulture, $"{i + 1}. {level}{top[i].Title} — {top[i].Platform}\n{top[i].Url}\n");
        }

        text.Append(CultureInfo.InvariantCulture, $"\n*Todos los titulares ({headlines.Count})*\n");
        foreach (var alert in headlines.OrderByDescending(alert => alert.EndTime).Take(MaxListed))
        {
            text.Append(CultureInfo.InvariantCulture, $"- {alert.Title} — {alert.Platform}\n{alert.Url}\n");
        }

        if (headlines.Count > MaxListed)
        {
            text.Append(CultureInfo.InvariantCulture, $"… y {headlines.Count - MaxListed} más en el portal.\n");
        }

        return text.ToString().TrimEnd();
    }
}
