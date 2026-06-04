# MediaOpsCore Worker

Week 1 scaffold for the Phase 1A operational unification effort.

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