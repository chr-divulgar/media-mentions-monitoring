using MediaOpsCore.Modules.Alerting.Application;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MediaOpsCore.Workers.Operations;

/// <summary>Tells the people who opted in that the platform itself (not a monitored outlet) needs attention.</summary>
public interface IPlatformErrorNotifier
{
    /// <summary>
    /// Best-effort: never throws, a failed notification must not take the caller down. WhatsApp
    /// first; whoever it fails to reach (or has no phone) gets an email instead. With
    /// <paramref name="viaWhatsApp"/> false (WhatsApp itself is the problem) it is email only.
    /// </summary>
    Task NotifyAsync(string message, string subject, bool viaWhatsApp = true, CancellationToken cancellationToken = default);
}

/// <summary>
/// Notifies every user of the `config.users` collection (the MongoDB copy of the users) with
/// notifyPlatformErrors = true (the toggle in Settings → Usuarios, apps/web-api): by WhatsApp when
/// it has a phone, by email as the backup (or when WhatsApp is down).
/// </summary>
public sealed class PlatformErrorNotifier : IPlatformErrorNotifier
{
    private readonly MongoConfigStore store;
    private readonly IMessageNotifier whatsApp;
    // Null when SMTP is not configured.
    private readonly IMessageNotifier? email;
    private readonly ILogger<PlatformErrorNotifier> logger;

    public PlatformErrorNotifier(
        MongoConfigStore store,
        IMessageNotifier whatsApp,
        IMessageNotifier? email,
        ILogger<PlatformErrorNotifier> logger)
    {
        this.store = store;
        this.whatsApp = whatsApp;
        this.email = email;
        this.logger = logger;
    }

    public async Task NotifyAsync(string message, string subject, bool viaWhatsApp = true, CancellationToken cancellationToken = default)
    {
        try
        {
            var users = await ListOptedInUsersAsync(cancellationToken).ConfigureAwait(false);
            if (users.Count == 0)
            {
                logger.LogInformation("[PlatformErrorNotifier] No user has platform-error notifications enabled; nothing sent.");
                return;
            }

            var needEmail = users.AsEnumerable();
            if (viaWhatsApp)
            {
                var phones = users.Where(user => user.Phone is not null).Select(user => user.Phone!).ToArray();
                var reached = phones.Length == 0
                    ? []
                    : await whatsApp.SendAsync(message, phones, null, cancellationToken).ConfigureAwait(false);
                logger.LogInformation("[PlatformErrorNotifier] WhatsApp reached {Sent}/{Total} user(s).", reached.Count, phones.Length);
                needEmail = users.Where(user => user.Phone is null || !reached.Contains(user.Phone));
            }

            var addresses = needEmail.Where(user => user.Email is not null).Select(user => user.Email!).ToArray();
            if (addresses.Length == 0)
            {
                return;
            }

            if (email is null)
            {
                logger.LogWarning("[PlatformErrorNotifier] {Count} user(s) need an email but SMTP is not configured.", addresses.Length);
                return;
            }

            var sent = await email.SendAsync(message, addresses, subject, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("[PlatformErrorNotifier] Email reached {Sent}/{Total} user(s).", sent.Count, addresses.Length);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "[PlatformErrorNotifier] Could not notify users of a platform error.");
        }
    }

    private async Task<IReadOnlyList<MongoDocumentMapper.OptedInUser>> ListOptedInUsersAsync(CancellationToken cancellationToken)
    {
        var documents = await store.Users
            .Find(Builders<BsonDocument>.Filter.Eq("notifyPlatformErrors", true))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return documents
            .Select(MongoDocumentMapper.TryMapOptedInUser)
            .Where(user => user is not null)
            .Select(user => user!)
            .ToArray();
    }
}
