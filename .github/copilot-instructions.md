# Copilot Instructions — Radio Alert

**Radio Alert** is a media monitoring system: it records radio broadcasts, transcribes audio via Google Speech Recognition (Python), summarizes content with OpenAI (gpt-5-mini), and distributes clips to WhatsApp via the `mudslide` CLI. The stack is a pnpm + Turborepo monorepo with NestJS/Fastify backend and React/Vite frontend.

---

## Commands

```bash
# Install
pnpm install

# Dev (both apps concurrently via Turbo)
pnpm dev

# Dev single app
pnpm --filter web-api dev       # NestJS watch mode, port 3001
pnpm --filter web-ui dev        # Vite, port 4200

# Lint / Format
pnpm lint
pnpm format                     # Prettier on **/*.{ts,tsx,md}

# Test (backend only — no frontend tests)
pnpm --filter web-api test
pnpm --filter web-api test:cov
pnpm --filter web-api test:e2e

# Run a single test file
pnpm --filter web-api test -- --testPathPattern=alerts.service

# Production build (Windows, order matters)
pnpm build:ui                   # web-ui → dist/
xcopy /E /I /Y apps\web-ui\dist apps\web-api\public
pnpm build:api                  # web-api (NODE_ENV=production)
pnpm start                      # starts web-api in prod mode
```

After changing anything in `packages/shared`, rebuild it first:
```bash
pnpm --filter @repo/shared build
```

## Graphify Policy (Mandatory)

After finishing any code changes in this repository, always refresh the project graph before closing the task.

Required behavior:

- Run `/graphify` after code edits are complete.
- Do not finalize the task until the graph refresh succeeds.
- If the refresh fails, report the failure and reason in the final status.
- Confirm `graphify-out/graph.json` and `graphify-out/manifest.json` were updated when those files exist in this repository.

---

## Architecture

```
apps/web-api/          NestJS + Fastify backend (port 3001)
apps/web-ui/           React 18 + Vite frontend (port 4200)
packages/shared/       Shared DTOs and helpers (@repo/shared)
packages/config-eslint/
packages/config-typescript/
scripts/               Python transcription scripts (getText.py, etc.)
```

### Backend (`apps/web-api`)

- **NestJS with Fastify adapter** — 10 MB body limit, 10-min timeout
- **MongoDB via TypeORM** (`MongoRepository`, not Mongoose). Named connection: `monitoring`. Entities live in `src/app/entities/`: `Alert`, `Note`, `Transcription`, `Platform`
- Services inject the DataSource by name: `@InjectDataSource('monitoring') private readonly dataSource: DataSource` and get repositories via `dataSource.getMongoRepository(Entity)`
- **Static serving**: In production, React build is copied to `apps/web-api/public/` so Fastify serves everything from a single origin
- **ConfigModule** loads `.env` (dev) or `.env.production` (prod) based on `NODE_ENV`; `synchronize: false` on TypeORM

Key modules: `AudioModule`, `AlertsModule`, `NotesModule`, `SettingsModule`, `ClientsModule`, `FirebaseAdminModule`, `AuthModule`

Key API routes:
| Method | Path | Purpose |
|--------|------|---------|
| POST | `/alerts` | List alerts with date/type filter |
| POST | `/alerts/getText` | Transcribe audio → upsert Note |
| POST | `/alerts/getSummary` | Summarize text via OpenAI → update Note |
| POST | `/audio/createFile` | Generate MP3/WAV segment via FFmpeg |
| GET  | `/audio/fetchByName/:filename` | Serve audio file |
| POST | `/notes/set-note` | Save/update Note metadata + build WhatsApp message |
| POST | `/notes/send-note` | Send note + audio to WhatsApp (mudslide) |
| GET  | `/settings/get-platforms/:media` | Get platform/slot config |
| POST | `/settings/update-platform` | Update platform settings |

### Frontend (`apps/web-ui`)

- **React 18, Vite 5, Ant Design 5** (Pro Layout + ProComponents), WaveSurfer.js for the audio editor
- **State**: React Context only — no Redux or Zustand. Contexts are in `src/context/`: `AuthContext`, `ThemeContext`
- **API calls**: All via a single Axios instance (`src/services/Agent.ts`) with `baseURL: import.meta.env.VITE_API_LOCAL`
- **Pages**: `alerts/`, `audio/` (waveform editor), `notes/`, `settings/`, `dashboard/`, `auth/`

### Shared Package (`packages/shared`)

TypeScript DTOs and helpers used by both apps. Key models: `alerts.dto`, `notes.dto`, `audio.dto`, `dashboard.dto`, `settings.dto`, `clients.dto`. Also exports `note.enum.ts` (`NoteOrigin`, `NoteSentiment`, etc.) and date/text helpers in `helper/`.

### Transcription Flow

1. Frontend requests `/alerts/getText` with a filename
2. Backend: splits WAV file into 60-second chunks (`audio-chunker.ts`), transcribes each chunk in parallel via `transcribeWithPython` (`google-speech.ts`), joins text, upserts a `Note` document
3. `/alerts/getSummary` sends the text to OpenAI (gpt-5-mini), parses title + summary from the response, updates the `Note`

---

## Key Conventions

- **MongoRepository pattern**: Never use `@InjectRepository` — always inject `DataSource` by name and call `dataSource.getMongoRepository(Entity)` in the constructor
- **Filename convention for audio**: `<platform>_<alertId>.wav` — the service extracts `alertId` as `filename.split('_')[1].replace('.wav', '')`
- **Audio files at runtime**: stored in `apps/web-api/audioFiles/` (relative to CWD of the running process)
- **Chunk cleanup**: After transcription, the temp chunks directory is deleted with `fs.rm(..., { recursive: true, force: true })`
- **WhatsApp dispatch**: Uses `cross-spawn` to run the `mudslide` CLI; paths are Windows-only
- **OpenAI model**: `gpt-5-mini` via `openai.responses.create()` (not `chat.completions`)
- **CORS origins**: `https://rpt-monitoreo.github.io`, `localhost:4200/4300`, `*.trycloudflare.com`

## Language Policy (Mandatory)

All code and code-adjacent technical documentation in `media-mentions-monitoring` must be written in **English**.

This includes:

- Class, interface, type, enum, function, method, variable, constant, and property names
- DTO/entity/model names and field names
- Inline comments, docstrings, JSDoc/TSDoc, and code examples
- Error messages intended for developers/operators
- ADRs, technical docs, and README sections related to implementation details

Allowed exceptions:

- User-facing text/content that is intentionally Spanish (UI labels, report templates, monitored content)
- Proper nouns, legal names, or contractual terms that must remain in original language

Pull request rule:

- Do not introduce new Spanish identifiers or in-code technical documentation.
- If touching legacy Spanish identifiers, prefer English names for new code and refactor old names when safe.

---

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

---

## Constraints

- **Windows-only**: mudslide CLI paths, audio recording scripts, and build commands (`set NODE_ENV=...`) are Windows-specific. No Docker.
- **Python required**: `scripts/getText.py` needs a Python env with `speech_recognition` and `google-cloud-speech`
- **TypeORM `synchronize: false`**: Schema changes must be applied manually to MongoDB
- **fastify pinned to 4.28.1** via pnpm overrides (breaking change in later versions)
