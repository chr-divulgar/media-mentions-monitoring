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

## Decisions recorded

- Use `.NET 10` SDK-style projects with nullable reference types and implicit usings enabled.
- Keep the worker host thin and limited to orchestration.
- Keep application projects free from infrastructure references.
- Keep domain projects isolated from application and infrastructure concerns.

## Next architectural step

Port ProcessGuardian helper behaviors (timeout, restart, reconciliation) and integrate stage persistence adapters for shadow execution.