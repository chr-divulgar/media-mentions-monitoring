namespace MediaOpsCore.Workers.Operations;

public sealed class FirestoreCaptureSourceRepositoryOptions
{
    /// <summary>
    /// Firebase/GCP project id (FIREBASE_PROJECT_ID) — same service-account JSON the NestJS
    /// backend already uses for Firestore (apps/web-api/.env), just read on this side too.
    /// </summary>
    public string? ProjectId { get; init; }

    /// <summary>
    /// Service account client email (FIREBASE_CLIENT_EMAIL) — the JWT issuer.
    /// </summary>
    public string? ClientEmail { get; init; }

    /// <summary>
    /// Service account private key, PEM-encoded (FIREBASE_PRIVATE_KEY). May contain literal
    /// "\n" sequences (as stored in a .env file) rather than real line breaks — normalized by
    /// GoogleServiceAccountTokenProvider before use.
    /// </summary>
    public string? PrivateKeyPem { get; init; }

    /// <summary>
    /// Firestore collection holding capture sources — the same "platforms" collection
    /// apps/web-api/src/app/settings/settings.service.ts already reads/writes.
    /// </summary>
    public string CollectionPath { get; init; } = "platforms";

    public int RequestTimeoutSeconds { get; init; } = 15;

    public bool IsEnabled =>
        !string.IsNullOrWhiteSpace(ProjectId) &&
        !string.IsNullOrWhiteSpace(ClientEmail) &&
        !string.IsNullOrWhiteSpace(PrivateKeyPem);
}
