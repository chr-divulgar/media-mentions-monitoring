using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MediaOpsCore.Workers.Operations;

/// <summary>
/// Audit trail for capture coverage: one Firestore document per clock hour, id = the hour key
/// ("2026-09-17_13"), in the same project and service account the rest of the system already uses.
/// The in-progress hour is refreshed periodically and then overwritten with the authoritative
/// numbers when the hour closes, so stopping the worker no longer erases what it had recorded.
///
/// The per-source detail travels as one JSON string field rather than a nested Firestore map.
/// ponytail: the REST API type-tags every value individually and this snapshot nests arrays of
/// objects two levels deep, which would need a generic serializer for no gain — one document per
/// hour is fetched whole and parsed anyway. If auditing ever needs Firestore to filter on a
/// per-source field server-side, that serializer (or a subcollection per source) is the upgrade.
/// </summary>
public sealed class FirestoreCaptureSummaryStore
{
    private readonly HttpClient httpClient;
    private readonly GoogleServiceAccountTokenProvider tokenProvider;
    private readonly FirestoreCaptureSourceRepositoryOptions options;
    private readonly ILogger<FirestoreCaptureSummaryStore> logger;
    private readonly string collectionUri;

    public FirestoreCaptureSummaryStore(
        HttpClient httpClient,
        GoogleServiceAccountTokenProvider tokenProvider,
        FirestoreCaptureSourceRepositoryOptions options,
        ILogger<FirestoreCaptureSummaryStore> logger)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        collectionUri =
            $"https://firestore.googleapis.com/v1/projects/{options.ProjectId}/databases/(default)/documents/captureHours";
    }

    /// <summary>
    /// Creates or overwrites the document for this hour. PATCH on a missing document creates it,
    /// and the hour key is the document id, so repeated writes for the same hour are idempotent —
    /// which is what lets both the periodic live refresh and the close-of-hour write target it.
    /// </summary>
    public async Task UpsertHourAsync(
        string hourKey,
        CaptureSummarySnapshot snapshot,
        bool isClosed,
        CancellationToken cancellationToken = default)
    {
        var body = new
        {
            fields = new Dictionary<string, object>
            {
                ["hourKey"] = FirestoreDocumentMapper.StringValue(hourKey),
                ["updatedAtUtc"] = FirestoreDocumentMapper.TimestampValue(DateTimeOffset.UtcNow),
                ["isClosed"] = FirestoreDocumentMapper.BoolValue(isClosed),
                ["sourceCount"] = FirestoreDocumentMapper.IntegerValue(snapshot.Sources.Length),
                ["averageCoveragePercent"] = FirestoreDocumentMapper.DoubleValue(
                    snapshot.Sources.Length == 0 ? 0 : Math.Round(snapshot.Sources.Average(s => s.CoveragePercent), 2)),
                ["snapshotJson"] = FirestoreDocumentMapper.StringValue(JsonSerializer.Serialize(snapshot)),
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Patch, $"{collectionUri}/{hourKey}")
        {
            Content = JsonContent.Create(body),
        };
        await AuthorizeAsync(request, cancellationToken).ConfigureAwait(false);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));

        using var response = await httpClient.SendAsync(request, cts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "[FirestoreCaptureSummaryStore] Could not persist capture summary for hour {HourKey}: {StatusCode}.",
                hourKey, response.StatusCode);
        }
    }

    private async Task AuthorizeAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await tokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
}
