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

## Decisions recorded

- Use `.NET 10` SDK-style projects with nullable reference types and implicit usings enabled.
- Keep the worker host thin and limited to orchestration.
- Keep application projects free from infrastructure references.
- Keep domain projects isolated from application and infrastructure concerns.

## Next architectural step

Add module-specific use cases and adapter registration for capture and segmentation once the shared ports are stabilized.