# YouTube Cookies Architecture

## Overview

Cookies for YouTube authentication are extracted via the NestJS backend (manual paste or an
automated Puppeteer login session) and consumed by the .NET worker, which uses them with `yt-dlp`
to resolve live stream URLs. Both sides agree on a single canonical shared location; the worker's
own HTTP endpoint is the way NestJS learns whether the worker is actually alive.

## Flow

```
User (Web UI)
  ↓
NestJS Backend (Port 3001)
  ├─ POST /settings/youtube/login-session/start        → opens a Puppeteer browser window
  ├─ POST /settings/youtube/login-session/obtain-cookies → extracts cookies from that session
  ├─ POST /settings/youtube/save-cookies                → manual cookie paste
  │     (all three validate, then write to the shared path and clear the local alert flag)
  ├─ POST /settings/youtube/sync-to-worker              → pushes current cookies to the worker over HTTP
  └─ GET  /settings/youtube/status                      → calls the worker's GET /youtube/health first;
        reports 'worker_unreachable' if that call fails, never inferring health from local files alone
          ↓
      Shared Storage (Project Root)
      └─ shared-cookies/youtube-cookies.txt
      └─ shared-cookies/youtube-auth-required.flag
          ↓
      .NET Worker
      ├─ POST /youtube/cookies  → receives pushed cookies, validates, writes, clears alert,
      │                           triggers an immediate reconciliation for excluded sources
      └─ GET  /youtube/health   → reports auth alert / cookie validity / excluded source state;
                                   the 200 response itself is the liveness signal
```

## Canonical file paths

There is exactly one shared-cookies location, resolved independently but consistently on both
sides — no fallback copies, no alternate directories:

- **NestJS** (`apps/web-api/src/app/settings/youtube.service.ts`): resolved from `__dirname`
  (stable regardless of whether the process was launched from the repo root or from
  `apps/web-api`), landing on `<repoRoot>/shared-cookies/youtube-cookies.txt` and
  `.../youtube-auth-required.flag`. Overridable via `YOUTUBE_COOKIES_PATH` /
  `YOUTUBE_ALERT_FLAG_PATH`.
- **Worker** (`apps/media-core-worker/stage/worker-options.json`):
  ```json
  {
    "youtubeCookiesFilePath": "../../shared-cookies/youtube-cookies.txt",
    "youtubeCookiesAlertFilePath": "../../shared-cookies/youtube-auth-required.flag"
  }
  ```
  resolved relative to the worker's application root, landing on the same
  `<repoRoot>/shared-cookies/` directory.

## Worker health and cookie push

Full request/response shapes for both worker routes (`POST /youtube/cookies`,
`GET /youtube/health`) are documented in `docs/YOUTUBE_COOKIES_HTTP_ENDPOINT_SPEC.md`. In short:

- Saving/extracting cookies in NestJS writes the shared file directly — that write does not
  depend on the worker being reachable.
- Pushing to the worker (`sync-to-worker`) is a secondary, explicit action that lets the worker
  react immediately (validate, clear its alert, retry currently-excluded sources) instead of
  waiting for its own next scheduled read of the shared file.
- Status (`GET /settings/youtube/status`) always asks the worker first. If the worker doesn't
  respond, the status is `worker_unreachable` — distinct from `unhealthy` (cookies expired) and
  `degraded` (cookies present but something's off) — so the UI never shows a stale "healthy" for
  a worker that simply isn't running.

## Testing

```bash
# 1. Extract cookies via the web UI: Start Login Session → Obtain Cookies → Send to Worker
# 2. Verify the shared location has cookies
ls -la shared-cookies/youtube-cookies.txt

# 3. Start the worker and confirm it resolves YouTube sources without auth errors
dotnet run --project apps/media-core-worker/src/Workers/Operations.Worker

# 4. Confirm liveness reporting: stop the worker, then poll status
curl http://localhost:3001/settings/youtube/status   # → status: "worker_unreachable"
```
