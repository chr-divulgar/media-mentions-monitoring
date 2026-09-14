# YouTube Cookies HTTP Endpoint Specification

## Overview

The .NET media-core-worker exposes two HTTP routes on `http://localhost:5000` for the YouTube
cookies workflow, implemented in
`apps/media-core-worker/src/Workers/Operations.Worker/YouTubeCookiesHttpService.cs` as a raw
`HttpListener`-based `IHostedService` (not ASP.NET Core/Kestrel — this endpoint predates and
doesn't need the full web framework). NestJS is the primary writer of the shared cookies file;
these two routes exist for (a) pushing a fresh cookies submission to the worker in real time and
(b) letting NestJS ask the worker directly whether it is alive and what it currently knows about
cookie/auth state.

---

## `POST /youtube/cookies`

Receives a fresh cookies submission, validates it, writes it to the canonical shared path, clears
the auth alert flag, and triggers an immediate reconciliation attempt for any currently-excluded
YouTube sources.

### Request

```json
{ "cookies": "# Netscape HTTP Cookie File\n\n.youtube.com\tTRUE\t/\tFALSE\t4070908800\tSAMESITE\tLax\n" }
```

Cookie format: Netscape HTTP Cookie File — one cookie per line, tab-separated fields (domain,
flag, path, secure, expiration, name, value), comments start with `#`.

### Responses (all bodies camelCase)

**200 OK** — accepted, written, reconciliation triggered in the background:
```json
{ "success": true, "message": "Cookies saved. Worker will attempt to recover any excluded YouTube sources within about a minute." }
```

**400 Bad Request** — missing/empty `cookies` field.

**422 Unprocessable Entity** — cookies failed validation (missing Netscape header, no
`youtube.com` domain cookie, or already expired) — validated via `IYouTubeCookiesValidator`
**before** anything is written to disk:
```json
{ "success": false, "message": "Cookies expired at 2026-01-01T00:00:00Z" }
```

**500 Internal Server Error** — `YoutubeCookiesFilePath` not configured on the worker.

### What it does, in order

1. Validate the submitted content (`IYouTubeCookiesValidator.ValidateCookiesAsync`) — reject
   invalid/expired cookies before touching disk.
2. Write the content verbatim, UTF-8, to `options.YoutubeCookiesFilePath` (the canonical shared
   path, resolved from `worker-options.json` — see below).
3. Clear the auth alert flag (`IYouTubeCookiesAlertService.ClearAlert()`).
4. Fire-and-forget an immediate reconciliation pass
   (`IYouTubeReconciliationTrigger.TriggerImmediateReconciliationAsync`) for any TV/YouTube
   sources currently excluded. This runs in the background rather than being awaited, since
   recovering several sources can involve multiple sequential yt-dlp calls (each up to
   `YtdlpResolutionTimeoutSeconds`) — far longer than the 10-second timeout NestJS's caller uses.
   Worst case, if this immediate attempt doesn't recover a source, the normal reconciliation
   cadence still applies (next hot-recovery minute, or the next scheduled tick at :00/:01/:30/:59).

---

## `GET /youtube/health`

Reports the worker's current YouTube auth/cookie/source-exclusion state. Receiving **any**
response at all — regardless of its content — is itself the liveness signal NestJS is checking
for; a connection refused/timeout means the worker is not running.

### Response (200 OK, camelCase)

```json
{
  "authAlertActive": false,
  "cookiesFileExists": true,
  "cookiesValid": true,
  "cookieCount": 42,
  "earliestExpiration": "2026-06-01T00:00:00Z",
  "hasYouTubeDomain": true,
  "excludedSourceIds": [],
  "totalTvSources": 3,
  "activeTvSources": 3,
  "message": "Valid: 42 cookies, YouTube domain, expires 2026-06-01T00:00:00Z"
}
```

Backed by `IYouTubeHealthSnapshotProvider`, which composes the same alert service, validator, and
TV-source filtering (`ILiveStreamUrlResolver.CanResolve`) already used elsewhere in the worker —
no separate state-tracking mechanism.

---

## Canonical cookies path

Both NestJS and the worker resolve the same physical directory:
`<repoRoot>/shared-cookies/youtube-cookies.txt` and `<repoRoot>/shared-cookies/youtube-auth-required.flag`.

- **Worker side**: `worker-options.json` → `youtubeCookiesFilePath` /
  `youtubeCookiesAlertFilePath`, resolved relative to the worker's application root
  (`OperationsWorkerOptionsLoader`).
- **NestJS side**: `YouTubeService.getSharedCookiesPath()` / `getSharedAlertPath()`, resolved from
  `__dirname` (stable regardless of the process's launch-time working directory) — overridable
  via `YOUTUBE_COOKIES_PATH` / `YOUTUBE_ALERT_FLAG_PATH` env vars for non-standard deployments.

There is no other cookies path in play. An older `apps/media-core-worker/stage/cookies/...`
fallback location existed in NestJS but was never read by the worker's resolver — it has been
removed rather than kept as a "just in case" fallback.

---

## Configuration

| Variable | Where | Default |
|---|---|---|
| `YOUTUBE_WORKER_ENDPOINT` | NestJS | `http://localhost:5000` |
| `YOUTUBE_COOKIES_PATH` | NestJS | derived from `__dirname` (see above) |
| `YOUTUBE_ALERT_FLAG_PATH` | NestJS | derived from `__dirname` (see above) |
| `youtubeCookiesFilePath` / `youtubeCookiesAlertFilePath` | Worker (`worker-options.json`) | `../../shared-cookies/...` |

The HTTP listener's port (5000) is hardcoded on the worker side, matching the NestJS default.

---

## Error handling

- NestJS's `sendCookiesToWorker()` uses a 10-second timeout; on failure (unreachable, non-2xx,
  validation rejection) cookies remain saved locally (the shared-file write already happened
  independently of this HTTP push) and the UI surfaces the worker's own rejection message.
- NestJS's `checkWorkerHealth()` (used by `GET /settings/youtube/status`) uses a 3-second
  timeout, since it runs on every status poll and must stay responsive.
- Neither call retries automatically; the frontend can always re-trigger a manual "Send to
  Worker" action.
