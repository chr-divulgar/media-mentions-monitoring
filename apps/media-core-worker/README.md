# MediaOpsCore Worker

Week 1 scaffold for the Phase 1A operational unification effort.

Current implementation includes continuous audio capture, startup stream validation with FFmpeg, failed-source discovery from `primaryUrl`, and persistence of recovered `streamUrl` values.

Push-Location "C:\Users\juanb\Documents\chr-divulgar\media-mentions-monitoring\apps\media-core-worker"; dotnet build "src\Workers\Operations.Worker\Operations.Worker.csproj"; dotnet ".\src\Workers\Operations.Worker\bin\Debug\net10.0\MediaOpsCore.Workers.Operations.Worker.dll"; Pop-Location

## Structure

- `src/BuildingBlocks` contains shared domain, application, and infrastructure baselines.
- `src/Modules` contains the Capture, Segmentation, and ProcessGuardian modules.
- `src/Workers/Operations.Worker` contains the long-running host.
- `tests/Unit` contains fast, isolated checks.
- `tests/Architecture` contains dependency boundary checks.

## Build

```bash
dotnet restore MediaOpsCore.sln
dotnet build MediaOpsCore.sln -c Release
dotnet test MediaOpsCore.sln -c Release
```

## Startup Stream Recovery

At startup, the worker executes source initialization before background workers start:

1. Validate every configured `streamUrl` using the internal FFmpeg validator.
2. For failed sources with `primaryUrl`, run discovery over the page content.
3. Revalidate the discovered candidate URL.
4. If valid, replace the runtime `streamUrl` and persist it into the capture source file.
5. Start continuous and discrete ingestion workers.

Relevant implementation:

- `src/Workers/Operations.Worker/StartupSourceInitializationService.cs`
- `src/Workers/Operations.Worker/FfmpegStartupStreamValidator.cs`
- `src/Workers/Operations.Worker/HttpStartupSourceDiscoveryService.cs`
- `src/Workers/Operations.Worker/StaticCaptureSourceProvider.cs`

## Discovery Rules

The HTTP discovery service currently extracts candidates from:

- Direct stream links with extensions: `.m3u8`, `.aac`, `.mp3`, `.pls`, `.m3u`.
- Generic endpoints containing `/stream` or `/live`.
- Escaped URLs embedded in scripts (for example `https:\/\/...`).
- `src`/`href` attribute values that resolve to stream-like URLs.

Static assets (`.js`, `.css`, images) are excluded from candidates.

## Key Worker Options

Startup behavior is controlled from `stage/worker-options.json`:

- `enableStartupValidation`
- `enableStartupDiscoveryOnFailedOnly`
- `startupValidationTimeoutSeconds`
- `startupDiscoveryRequestTimeoutSeconds`

Source file schema supports optional `primaryUrl` in `stage/capture-sources.json`.