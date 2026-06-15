namespace MediaOpsCore.Workers.Operations;

public sealed class FirebaseCaptureSourceRepositoryOptions
{
    /// <summary>
    /// Absolute URI of the Firebase Realtime Database root.
    /// Example: https://my-project-default-rtdb.firebaseio.com
    /// When null or empty, Firebase is disabled and the JSON file fallback is used exclusively.
    /// </summary>
    public string? BaseUrl { get; init; }

    /// <summary>
    /// Path within the Firebase Realtime Database that holds the platforms dictionary.
    /// Defaults to "platforms".
    /// </summary>
    public string PlatformsPath { get; init; } = "platforms";

    /// <summary>
    /// Optional Firebase auth token appended as ?auth=... query parameter.
    /// </summary>
    public string? AuthToken { get; init; }

    /// <summary>
    /// HTTP request timeout for Firebase reads. Defaults to 15 seconds.
    /// </summary>
    public int RequestTimeoutSeconds { get; init; } = 15;

    public bool IsEnabled => !string.IsNullOrWhiteSpace(BaseUrl);
}
