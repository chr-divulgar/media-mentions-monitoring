# Firebase Configuration for MediaOpsCore Worker

## Overview

Firebase Realtime Database is an optional, interchangeable primary store for capture sources. The worker implements **Hexagonal Architecture** principles, so Firebase can be swapped with MongoDB or any other database via interface contracts.

**Configuration priority order**:
1. **Environment variables** (`.env` or system) — highest priority
2. **`stage/worker-options.json`** — fallback if env vars not set
3. **JSON file only** — if Firebase is disabled or unavailable

When both env vars and JSON config are present, **env vars override JSON values**. This is the recommended pattern for secure secret management:
- Commit `stage/worker-options.json` with `null` placeholders
- Keep real credentials in `.env` (git-ignored)
- Worker dynamically selects the storage backend at startup

## Setup

### 1. Copy the Template

```bash
cp .env.example .env
```

### 2. Set Firebase Credentials

Edit `.env` and provide your Firebase Realtime Database credentials:

```bash
# Firebase Realtime Database configuration (optional)
FIREBASE_BASE_URL="https://your-project-default-rtdb.firebaseio.com"
FIREBASE_AUTH_TOKEN="your-firebase-auth-token"
FIREBASE_PLATFORMS_PATH="platforms"                      # optional, defaults to "platforms"
FIREBASE_REQUEST_TIMEOUT_SECONDS="15"                    # optional, defaults to 15
```

**Environment Variable Details**:

| Variable | Required | Default | Purpose |
|----------|----------|---------|---------|
| `FIREBASE_BASE_URL` | Yes (to enable) | — | Root URI of Firebase Realtime DB (e.g., `https://my-project-default-rtdb.firebaseio.com`) |
| `FIREBASE_AUTH_TOKEN` | Yes (to enable) | — | Firebase auth token for read/write operations |
| `FIREBASE_PLATFORMS_PATH` | No | `platforms` | Path within Firebase where capture sources are stored |
| `FIREBASE_REQUEST_TIMEOUT_SECONDS` | No | `15` | HTTP timeout in seconds for Firebase reads/writes |

### 3. Google Speech Recognition API (Transcription)

If you need audio transcription, also set:

```bash
GOOGLE_SPEECH_API_KEY="your-google-speech-api-key"
```

### 4. Never Commit Secrets

**Important**: Add `.env` to `.gitignore` (it is already in the project `.gitignore`).

Example of safe commit state:
- ✅ `stage/worker-options.json` with `"baseUrl": null, "authToken": null`
- ✅ `.env.example` with placeholder values
- ❌ `.env` with real credentials (not in repo)

## How It Works

### Startup Sequence

1. **OperationsWorkerOptionsLoader** reads configuration:
   - First checks environment variables (`FIREBASE_BASE_URL`, `FIREBASE_AUTH_TOKEN`)
   - If not set, falls back to `stage/worker-options.json`
   - If Firebase is enabled, registers `FallbackCaptureSourceRepository` (dual-write)
   - If Firebase is disabled, uses `JsonFileCaptureSourceRepository` only

2. **Repository Selection** (Program.cs):
   ```csharp
   if (options.FirebaseDatabase?.IsEnabled == true)
   {
       services.AddSingleton<ICaptureSourceRepository>(
           sp => new FallbackCaptureSourceRepository(
               new FirebaseCaptureSourceRepository(...),
               new JsonFileCaptureSourceRepository(...),
               logger));
   }
   else
   {
       services.AddSingleton<ICaptureSourceRepository>(
           sp => new JsonFileCaptureSourceRepository(...));
   }
   ```

3. **Dual-Write Pattern** (when Firebase enabled):
   - Write to Firebase first (primary)
   - Always write to JSON file (fallback mirror)
   - Logs warnings if Firebase write fails but file write succeeds
   - Returns success if either write succeeds

### Database Interchangeability

All storage operations go through the `ICaptureSourceRepository` interface:

```csharp
public interface ICaptureSourceRepository
{
    Task<IReadOnlyList<CaptureSource>> ListAllAsync(CancellationToken ct = default);
    Task<bool> UpdateStreamUrlAsync(string sourceId, string streamUrl, CancellationToken ct = default);
    Task<bool> UpdateFallbackUrlsAsync(string sourceId, IEnumerable<string> fallbacks, CancellationToken ct = default);
    Task<bool> UpdateExclusionAsync(string sourceId, bool excluded, CancellationToken ct = default);
}
```

Adapters:
- `FirebaseCaptureSourceRepository` — Firebase Realtime DB via REST API
- `JsonFileCaptureSourceRepository` — Local JSON file (`stage/capture-sources.json`)
- `FallbackCaptureSourceRepository` — Decorator coordinating dual-write

**This architecture allows future migration to MongoDB, PostgreSQL, or cloud providers without changing business logic.**

## Troubleshooting

### Firebase writes failing silently
- Check `.env` for valid `FIREBASE_BASE_URL` and `FIREBASE_AUTH_TOKEN`
- Check network access to Firebase (firewall, VPN, etc.)
- Monitor logs for warnings: "Firebase update failed but JSON fallback succeeded"
- JSON file fallback is always written, so sources remain accessible

### Worker starting with JSON-only mode
- Verify `FIREBASE_BASE_URL` and `FIREBASE_AUTH_TOKEN` are set
- Check if they contain whitespace or quotes that need escaping
- Try setting one env var to an invalid value to see detailed error logs
- Confirm `.env` file exists and is readable

### Performance degradation
- Increase `FIREBASE_REQUEST_TIMEOUT_SECONDS` if Firebase requests timeout
- Check Firebase quota and throttling limits
- Monitor network latency to Firebase endpoint

## Environment Variable Precedence

The loader uses this precedence:

```csharp
var firebaseBaseUrl = Environment.GetEnvironmentVariable("FIREBASE_BASE_URL");
var firebaseAuthToken = Environment.GetEnvironmentVariable("FIREBASE_AUTH_TOKEN");

// If env vars provided, use them (highest priority)
if (!string.IsNullOrWhiteSpace(firebaseBaseUrl) && !string.IsNullOrWhiteSpace(firebaseAuthToken))
{
    options.FirebaseDatabase = new FirebaseCaptureSourceRepositoryOptions
    {
        BaseUrl = firebaseBaseUrl.Trim(),
        AuthToken = firebaseAuthToken,
        // ...
    };
}
// Fallback: use JSON config if no env vars provided
else if (model.FirebaseDatabase is { } fb && !string.IsNullOrWhiteSpace(fb.BaseUrl))
{
    options.FirebaseDatabase = new FirebaseCaptureSourceRepositoryOptions
    {
        BaseUrl = fb.BaseUrl.Trim(),
        AuthToken = fb.AuthToken,
        // ...
    };
}
```

This pattern ensures:
- Env vars always override JSON (for secrets management)
- JSON serves as fallback for shared/development configs
- Null/empty env vars don't disable Firebase if JSON config has values
- Both sources can coexist without conflicts
