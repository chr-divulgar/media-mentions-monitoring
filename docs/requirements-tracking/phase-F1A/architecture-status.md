# Phase 1A Architecture Status

## Week 2 baseline

The worker solution now establishes a modular hexagonal layout under `apps/media-core-worker` and introduces the first shared contracts:

- `BuildingBlocks.Domain`
- `BuildingBlocks.Application`
- `BuildingBlocks.Infrastructure`
- `Capture` module
- `Segmentation` module
- `ProcessGuardian` module
- `Operations.Worker`

The second week adds the first provider-agnostic persistence port, the process runner port, the initial Firebase adapter, and adapter contract tests.

Weeks 3-4 add capture and segmentation use cases in module application layers, while `Operations.Worker` stays focused on orchestration.

Base metrics are emitted through an application-level metrics port and a worker-side meter implementation for capture attempts/failures, generated segments, pipeline lag, and critical errors.

Week 5 ports helper behaviors into `ProcessGuardian.Application` with three explicit use cases: process supervision (timeout/restart), inactive reconciliation, and chunk orphan monitoring.

The worker orchestrates these use cases through application ports (`IProcessStateRepository`, `IProcessInspector`) so dependency direction remains unchanged.

Week 6 introduces stage-oriented adapters for database mirroring and filesystem evidence writing to support operational observability and controlled rollout readiness.

Week 7 adds canary controls at source-selection level with configurable 10-20% platform rollout and explicit rollback criteria based on stability metrics.

Week 8 extends canary execution to 20-50-100 progression under operational guardrails while preserving module boundaries and use-case orchestration flow.

The ingestion model is defined as shared/global for capture and segmentation sources, while tenant scope is reserved for downstream alerting, subscription, and client-facing consumption views.

## Decisions recorded

- Use `.NET 10` SDK-style projects with nullable reference types and implicit usings enabled.
- Keep the worker host thin and limited to orchestration.
- Keep application projects free from infrastructure references.
- Keep domain projects isolated from application and infrastructure concerns.
- Keep ingestion data global and apply tenant partitioning at alerting/consumption boundaries.

## Next architectural step

Start Phase 2 API host implementation (`Operations.Api.Host`) over existing application use cases without moving business rules out of Domain/Application layers.