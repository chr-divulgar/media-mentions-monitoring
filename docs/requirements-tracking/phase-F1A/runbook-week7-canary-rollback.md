# Week 7 Canary Runbook and Rollback

## Purpose

This runbook defines the operational procedure to execute Week 7 canary rollout for `media-core-worker` with explicit rollback criteria.

## Scope

- Canary execution for 10-20% platform subset.
- Shadow parity and canary tuning validation.
- Controlled rollback when parity or stability gates are violated.

## Preconditions

1. `dotnet test MediaOpsCore.sln -c Release` passes.
2. Stage persistence and filesystem evidence paths are available.
3. Legacy snapshot input file is available at configured `LegacySnapshotFilePath`.
4. Monitoring dashboards include at least:
   - `capture_success_rate`
   - `segment_generation_rate`
   - `pipeline_lag_seconds`
   - `process_orphan_count`
   - `critical_error_rate`

## Canary Configuration

Set worker options before rollout:

- `EnableCanaryMode=true`
- `CanaryPlatformMinPercent=10`
- `CanaryPlatformMaxPercent=20`
- `CanaryPlatformPercent=10` for first canary window
- `ShadowParityMinimumPercent=95`
- `EnableShadowMode=true`

Optional platform scoping:

- Use `CanaryPlatformAllowList` to restrict initial canary to selected platforms.

## Execution Steps

1. Start worker in stage with canary enabled.
2. Verify parity evidence files are generated under `StageFilesystemRootPath/shadow/`.
3. Observe tuning evidence files (`canary-tuning-*.json`) per cycle.
4. Keep canary running for the agreed observation window.
5. If parity remains above threshold and stability KPIs hold, allow tuning to move toward 20%.

## Promotion Criteria

Promote from 10% to 20% only when all conditions hold during the observation window:

1. Overall parity >= configured threshold.
2. No sustained increase in `critical_error_rate`.
3. `pipeline_lag_seconds` remains within operational limit.
4. No unresolved process orphan growth trend.

## Rollback Triggers

Trigger immediate rollback when one or more conditions occur:

1. Overall parity drops below threshold for sustained cycles.
2. Critical errors exceed baseline guardrail.
3. Capture or segmentation regressions are detected in functional evidence.
4. Operational SLA risk is declared by on-call owner.

## Rollback Procedure

1. Disable canary:
   - Set `EnableCanaryMode=false` and restart worker.
2. Keep `EnableShadowMode=true` to continue diagnostics without canary traffic.
3. Preserve all generated evidence files for post-mortem.
4. Open incident log with timestamp, impacted platforms, and parity reports.
5. Revert to last known stable configuration snapshot.

## Post-Rollback Actions

1. Analyze parity and tuning evidence to identify root cause.
2. Define a corrective patch and validate in stage.
3. Re-run canary from 10% with reduced scope if needed (`CanaryPlatformAllowList`).
4. Update compliance and architecture tracking documents with findings.
