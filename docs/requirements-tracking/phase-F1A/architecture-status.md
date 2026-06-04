# Phase 1A Architecture Status

## Week 1 baseline

The initial scaffold establishes a modular hexagonal layout under `apps/media-core-worker`:

- `BuildingBlocks.Domain`
- `BuildingBlocks.Application`
- `BuildingBlocks.Infrastructure`
- `Capture` module
- `Segmentation` module
- `ProcessGuardian` module
- `Operations.Worker`

## Decisions recorded

- Use `.NET 10` SDK-style projects with nullable reference types and implicit usings enabled.
- Keep the worker host thin and limited to orchestration.
- Keep application projects free from infrastructure references.
- Keep domain projects isolated from application and infrastructure concerns.

## Next architectural step

Add ports, use cases, and adapter registration once the first real capture flow is ported.