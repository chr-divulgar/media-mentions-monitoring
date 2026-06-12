# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

---

## Graphify — Codebase Navigation

Before answering questions about architecture, structure, components, or how to add/modify/find code,
check if `graphify-out/graph.json` exists and use it:

- `graphify query "<question>"` — broad BFS traversal
- `graphify path "<A>" "<B>"` — relationship between two concepts
- `graphify explain "<concept>"` — focused explanation of a symbol or module

Triggers: "how do I…", "where is…", "what does … do", "add/modify a component", "explain the architecture".

If `graphify-out/wiki/index.md` exists, use it for navigation. Read source files only when modifying
specific code, when the graph lacks detail, or when the graph is missing/stale.

---

## Architecture Overview

This monorepo contains two systems that share domain concepts but have independent deployment targets:

### 1. Legacy NestJS API + React UI (`apps/web-api`, `apps/web-ui`)

A pnpm + Turborepo monorepo with a NestJS/Fastify backend and React/Vite frontend for alert
management, transcription review, and WhatsApp distribution.

**Stack**: NestJS + Fastify (port 3001), MongoDB/TypeORM, React 18 + Vite 5 + Ant Design 5.

**Key Modules**: `AudioModule`, `AlertsModule`, `NotesModule`, `SettingsModule`, `ClientsModule`.

**API routes**:
| Method | Path | Purpose |
|--------|------|---------|
| POST | `/alerts` | List alerts |
| POST | `/alerts/getText` | Transcribe audio (spawns Python) |
| POST | `/alerts/getSummary` | Summarize text (OpenAI) |
| POST | `/audio/createFile` | Generate MP3/WAV segment via FFmpeg |
| GET  | `/audio/fetchByName/:filename` | Serve audio file |
| POST | `/notes/set-note` | Save/update note metadata |
| POST | `/notes/send-note` | Send note + audio to WhatsApp (mudslide) |
| GET  | `/settings/get-platforms/:media` | Get platform/slot config |

**Key constraints**:
- Windows-only deployment (mudslide CLI, hardcoded paths).
- Python required for `scripts/getText.py` (Google Speech Recognition).
- TypeORM MongoDB driver (legacy; not Mongoose).
- Production: UI built into `apps/web-api/public/` before API build.

### 2. .NET 10 Media Core Worker (`apps/media-core-worker`)

A modular .NET 10 Background Service that continuously captures live audio/video streams,
segments them into chunks, and monitors the capture processes. It follows Hexagonal (Clean)
Architecture with strict module boundaries.

```
src/
  BuildingBlocks/             Shared abstractions (Domain, Application, Infrastructure)
  Modules/
    Capture/                  Core audio/video ingestion (Domain + Application + Infrastructure)
    ProcessGuardian/          Child-process monitoring and reconciliation
    Segmentation/             Incremental audio segmentation into time-bounded chunks
  Workers/
    Operations.Worker/        Host: DI wiring, startup discovery/validation, worker loops
```

**Worker runtime loop** (30s heartbeat):
1. Continuous Capture → captures FLAC windows from live streams via FFmpeg
2. Incremental Segmentation → slices FLAC windows into OPUS segments (30s flush, 1h rotation)
3. ProcessGuardian → monitors child FFmpeg processes, reconciles stale/dead captures

**Key domain models**:
- `CaptureSource` — `SourceId`, `Platform`, `Media`, `StreamUrl`, `PrimaryUrl`, `FallbackStreamUrls`, `Country`, `UtcOffsetMinutes`, `IsExcluded`
- `PluginProfile` — `PluginId`, `Media`, `Platform`, `IngestionMode`, `FlacWindowDuration`, `OpusFlushInterval`, `OpusRotationInterval`
- `IngestionMode` — `Continuous` (0) | `Discrete` (1)

**Startup initialization flow**:
1. `StartupSourceInitializationService.InitializeAsync()` validates each configured `streamUrl` via FFmpeg.
2. For sources with a `primaryUrl`, `HttpStartupSourceDiscoveryService` runs HTML-scraping heuristics to discover/refresh fallback stream URLs (fire-and-forget after startup).
3. Invalid sources are marked `excluded: true` in `stage/capture-sources.json` for diagnosis.

**Supported media types** (via `plugin-profiles.json`):
- `radio` — default codec profile, all Colombian radio stations
- `video` — generic video stream (RTSP/HLS)
- `youtube` / `video` — YouTube-specific profile (currently defined, not yet populated)

**Stage configuration files** (checked into `stage/`):
- `capture-sources.json` — source registry (sourceId, platform, media, streamUrl, primaryUrl, fallbackStreamUrls)
- `plugin-profiles.json` — media/platform → codec mapping
- `worker-options.json` — runtime tuning (heartbeat, parallelism, FLAC silence chunking, output paths)

---

## Common Commands

### NestJS / Node.js (legacy system)

```bash
pnpm install                        # Install all dependencies
pnpm dev                            # Run all apps concurrently via Turbo
pnpm --filter web-api dev           # Backend only (NestJS, port 3001)
pnpm --filter web-ui dev            # Frontend only (Vite, port 4200)
pnpm lint                           # Lint all packages
pnpm format                         # Format TypeScript and Markdown
pnpm --filter web-api test          # Backend tests
pnpm --filter web-api test:cov      # With coverage
pnpm --filter web-api test:e2e      # E2E tests
pnpm build:ui && pnpm build:api     # Production build
pnpm start                          # Start backend in production
```

### .NET 10 Worker

```bash
cd apps/media-core-worker

dotnet build                        # Build solution
dotnet test                         # Run all tests (unit + integration + architecture)
dotnet run --project src/Workers/Operations.Worker   # Run worker locally
```

---

## Environment Variables

**NestJS backend** (`apps/web-api/.env`):
```
MONGODB_URI=mongodb://localhost:27017
BACK_PORT=3001
OPEN_AI_KEY=<key>
NODE_ENV=development|production
```

**React frontend** (`apps/web-ui/.env`):
```
VITE_API_LOCAL=http://localhost:3001
VITE_PORT=4200
VITE_FIREBASE_API_KEY=...
VITE_FIREBASE_AUTH_DOMAIN=...
```

---

## .NET 10 Architecture and Coding Standards

### Mandatory for all .NET work

Full details in `CODING_STANDARDS_DOTNET10.md` (repo root). Summary:

**Dependency rule** (non-negotiable):
- Domain depends on nothing.
- Application depends on Domain.
- Infrastructure depends on Application + Domain.
- Presentation/Workers depend on Application only.

**Module contract pattern** — every module exposes:
- Domain entities / value objects
- Application use cases (commands/queries) + inbound ports (interfaces)
- Outbound ports (interfaces for DB, queues, external tools, process runners)
- Infrastructure adapters implementing outbound ports

**Forbidden**:
- Business logic in controllers, workers, or repositories.
- Direct infrastructure dependency inside Domain.
- Silent catch blocks.
- `TODO` in critical flows without a linked backlog item.

**Worker-specific rules**:
- Worker host only orchestrates use cases — no business logic.
- All long-running loops must support `CancellationToken`.
- Retry with backoff for transient faults.
- Idempotency required for reprocessing.

**TDD (mandatory for new behavior)**:
1. Red: write failing test expressing behavior.
2. Green: minimal implementation to pass.
3. Refactor: improve design, tests stay green.

Test layers: Unit (domain/application, fast), Integration (adapters: DB/queue/process runners),
Contract (API/external), Architecture (dependency rules via ArchUnitNET or NetArchTest).

### Definition of Done

- Requirement mapped to module/use case.
- TDD cycle evidence in commit history.
- Tests pass locally and in CI.
- Structured logging + metrics added.
- No architecture rule violations.

---

## Requirements-First Implementation (Mandatory)

Before proposing or generating code, use these files as primary inputs:

- `3. ANEXO_TECNICO_MONITOREO_FINAL_25-05-2026 (2) Posperidad social.md`
- `Ficha Tecnica Monitoreo de Medios.md`
- `REQUIREMENTS_LIVE_MATRIX.md`
- `PROPOSAL_CONTEXT_BASE.md`

**Mandatory behavior**:
1. Map each change to one or more Requirement IDs (`RQ-xxx` or `A-xxx`).
2. If no Requirement ID exists, propose a matrix update first, then code.
3. Include acceptance evidence per requirement (tests, exports, logs, reports).
4. Flag contractual ambiguities explicitly as assumptions.

**Required format for implementation proposals**:
- Requirement IDs impacted
- Modules / ports / adapters affected
- Files to create / modify
- Tests / evidence to add
- Rollout and rollback notes

---

## Language Policy

All code and code-adjacent technical documentation for `media-mentions-monitoring` must be in **English**.

This includes: class/interface/type/enum/function/method/variable/constant/property names, DTO/entity
field names, inline comments, docstrings, TSDoc/JSDoc, developer-facing log messages.

**Exceptions**: user-facing text intentionally in Spanish (UI labels, report content, monitored content);
proper nouns and legal/contract terms that must remain in original language.

Do not introduce new Spanish identifiers. When touching legacy Spanish identifiers, use English for
new code and refactor legacy names when safe.

---

## Shared Package (`packages/shared`)

Exports DTOs (TypeScript interfaces/types) and helpers consumed by both Node apps:
- `models/` — `alerts.dto`, `notes.dto`, `audio.dto`, `dashboard.dto`, `settings.dto`, `clients.dto`
- `helper/` — date utilities, text transformations

After modifying `packages/shared`:
```bash
pnpm --filter @repo/shared build
```
