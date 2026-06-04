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

Week 6 introduces stage-oriented adapters for database mirroring and filesystem evidence writing, plus an application-level parity use case used by worker shadow mode.

Shadow execution now emits parity artifacts by collection into stage evidence paths while preserving module boundaries through ports (`ILegacySnapshotProvider`, `IEvidenceFileStore`, `IFunctionalParityUseCase`).

Week 7 adds canary controls at source selection level with configurable 10-20% platform rollout, and introduces a tuning component that adjusts canary percentage according to parity outcomes.

The worker persists canary tuning evidence alongside shadow parity artifacts to support operational decisions and rollback criteria.

Week 8 extends canary control with staged promotion milestones (20-50-100) gated by sustained parity success cycles, and rollback to the previous milestone when parity drops below threshold.

This keeps rollout policy isolated in the worker adapter layer (`CanaryRolloutTuner`) while preserving module boundaries and use-case orchestration flow.

## Decisions recorded

- Use `.NET 10` SDK-style projects with nullable reference types and implicit usings enabled.
- Keep the worker host thin and limited to orchestration.
- Keep application projects free from infrastructure references.
- Keep domain projects isolated from application and infrastructure concerns.

## Next architectural step

Start Phase 2 API host implementation (`Operations.Api.Host`) over existing application use cases without moving business rules out of Domain/Application layers.