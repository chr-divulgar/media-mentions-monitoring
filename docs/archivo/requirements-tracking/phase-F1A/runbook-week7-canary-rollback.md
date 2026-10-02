# Week 7 Canary Runbook and Rollback

## Purpose

This runbook defines the operational procedure to execute Week 7 canary rollout for `media-core-worker` with explicit rollback criteria.

## Scope

- Canary execution for 10-20% platform subset.
- Validation of operational stability during canary windows.
- Controlled rollback when stability gates are violated.

## Preconditions

1. `dotnet test MediaOpsCore.sln -c Release` passes.
2. Stage persistence and filesystem evidence paths are available.
3. Monitoring dashboards include at least:
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

Optional platform scoping:

- Use `CanaryPlatformAllowList` to restrict initial canary to selected platforms.

## Execution Steps

1. Start worker in stage with canary enabled.
2. Verify stage evidence outputs and operational logs are generated during execution.
3. Keep canary running for the agreed observation window.
4. If stability KPIs hold, move canary toward 20% according to rollout policy.

## Promotion Criteria

Promote from 10% to 20% only when all conditions hold during the observation window:

1. `critical_error_rate` remains within agreed guardrail.
2. Capture success and segment generation remain within expected baseline.
3. `pipeline_lag_seconds` remains within operational limit.
4. No unresolved process orphan growth trend.

## Rollback Triggers

Trigger immediate rollback when one or more conditions occur:

1. Critical errors exceed baseline guardrail.
2. Capture or segmentation regressions are detected in functional evidence.
3. Operational SLA risk is declared by on-call owner.

## Rollback Procedure

1. Disable canary:
   - Set `EnableCanaryMode=false` and restart worker.
2. Preserve all generated evidence files for post-mortem.
3. Open incident log with timestamp and impacted platforms.
4. Revert to last known stable configuration snapshot.

## Post-Rollback Actions

1. Analyze execution evidence and metrics to identify root cause.
2. Define a corrective patch and validate in stage.
3. Re-run canary from 10% with reduced scope if needed (`CanaryPlatformAllowList`).
4. Update compliance and architecture tracking documents with findings.
