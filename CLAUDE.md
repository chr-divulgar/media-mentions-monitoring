# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

**Radio Alert** — a media monitoring system that records radio broadcasts, transcribes audio via Google Speech Recognition (Python), summarizes content with OpenAI, and distributes clips via WhatsApp (mudslide CLI). It is a pnpm + Turborepo monorepo with a NestJS/Fastify backend and a React/Vite frontend.

## Common Commands

```bash
# Install all dependencies
pnpm install

# Run everything in dev mode (both apps concurrently via Turbo)
pnpm dev

# Run only backend (NestJS watch mode, port 3001)
pnpm --filter web-api dev

# Run only frontend (Vite, port 4200)
pnpm --filter web-ui dev

# Lint all packages
pnpm lint

# Format TypeScript and Markdown
pnpm format

# Run backend tests
pnpm --filter web-api test
pnpm --filter web-api test:cov       # with coverage
pnpm --filter web-api test:e2e       # e2e tests

# Production build sequence
pnpm build:ui                          # builds web-ui → dist/
pnpm build:api                         # builds web-api (NODE_ENV=production)

# Start backend in production
pnpm start
```

## Architecture

### Monorepo Structure

```
apps/web-api/       NestJS + Fastify backend
apps/web-ui/        React 18 + Vite + Ant Design frontend
packages/shared/    Shared DTOs and helpers (@repo/shared)
packages/config-eslint/
packages/config-typescript/
scripts/            Python transcription scripts and platform JSON configs
```

### Backend (`apps/web-api`)

- **Framework**: NestJS with Fastify adapter (10 MB body limit, 10-min timeout)
- **Database**: MongoDB via TypeORM, connection named `monitoring`; a second connection named `config` is available
- **Entities**: `Alert`, `Note`, `Transcription`, `Platform`
- **Modules**: `AudioModule`, `AlertsModule`, `NotesModule`, `SettingsModule`, `ClientsModule`, `FirebaseAdminModule`
- **Static serving**: The compiled React build is served from `apps/web-api/public/` — in production the frontend and backend share a single origin
- **CORS origins**: `https://rpt-monitoreo.github.io`, `localhost:4200/4300`, `*.trycloudflare.com`

Key API routes:
| Method | Path | Purpose |
|--------|------|---------|
| POST | `/alerts` | List alerts (date/type filtering) |
| POST | `/alerts/getText` | Transcribe audio (spawns Python) |
| POST | `/alerts/getSummary` | Summarize text (OpenAI) |
| POST | `/audio/createFile` | Generate MP3/WAV segment via FFmpeg |
| GET  | `/audio/fetchByName/:filename` | Serve audio file |
| POST | `/notes/set-note` | Save/update note metadata |
| POST | `/notes/send-note` | Send note + audio to WhatsApp (mudslide) |
| GET  | `/settings/get-platforms/:media` | Get platform/slot config |
| POST | `/settings/update-platform` | Update platform settings |

### Frontend (`apps/web-ui`)

- **Stack**: React 18, Vite 5, Ant Design 5 (Pro Layout + ProComponents), WaveSurfer.js for audio editing
- **State**: React Context (no Redux/Zustand)
- **API communication**: Axios clients in `src/services/`, pointing at `VITE_API_LOCAL`
- **Pages**: `alerts/`, `audio/` (waveform editor), `notes/`, `settings/`, `dashboard/`, `auth/`

### Shared Package (`packages/shared`)

Exports DTOs (TypeScript interfaces/types) and helpers consumed by both apps:
- `models/` — `alerts.dto`, `notes.dto`, `audio.dto`, `dashboard.dto`, `settings.dto`, `clients.dto`
- `helper/` — date utilities (dayjs/moment), text transformations

After modifying `packages/shared`, rebuild it before running the apps:
```bash
pnpm --filter @repo/shared build
```

### Python Integration

Audio transcription is handled by `scripts/getText.py` (Google Speech Recognition). The backend spawns it as a child process. FFmpeg (via `fluent-ffmpeg` + `ffmpeg-static`) is required for audio segment/fragment creation.

## Environment Variables

**Backend** (`apps/web-api/.env`):
```
MONGODB_URI=mongodb://localhost:27017
BACK_PORT=3001
OPEN_AI_KEY=<key>
NODE_ENV=development|production
```

**Frontend** (`apps/web-ui/.env`):
```
VITE_API_LOCAL=http://localhost:3001
VITE_PORT=4200
VITE_FIREBASE_API_KEY=...
VITE_FIREBASE_AUTH_DOMAIN=...
```

## Key Constraints

- **Windows-only deployment**: Some paths (mudslide CLI for WhatsApp, audio recording scripts) are hardcoded Windows paths. No Docker setup exists.
- **Python required**: `getText.py` depends on a Python environment with `speech_recognition` and `google-cloud-speech` installed.
- **Production build flow**: UI build output goes into `apps/web-api/public/` before API build, so Fastify serves it statically.
- **TypeORM with MongoDB**: Uses the legacy TypeORM MongoDB driver (not Mongoose). Entities in `apps/web-api/src/app/entities/`.
