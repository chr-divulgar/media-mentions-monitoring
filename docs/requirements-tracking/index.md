# Requirements Tracking Index

This index tracks the Phase 1A operational unification work for MediaOpsCore.

## Phase 1A

- [Architecture status](phase-F1A/architecture-status.md)
- [Compliance status](phase-F1A/compliance-status.md)

## Week 1 scope

- Create the .NET 10 solution scaffold.
- Establish the layered project structure.
- Add architecture boundary tests.
- Add the first CI workflow for build and test.

## Week 2 scope

- Add provider-agnostic persistence contracts and process runner ports.
- Add the initial Firebase adapter and adapter contract tests.
- Keep the worker host thin while the first real ports are introduced.

## Week 3-4 scope

- Port continuous capture flow into `Capture.Application` use cases.
- Port incremental segmentation flow into `Segmentation.Application` use cases.
- Instrument base operational metrics for errors, lag, and generated segments.

## Week 5 scope

- Port process supervision policies (timeout and restart) into `ProcessGuardian.Application`.
- Port inactive reconciliation use case.
- Port chunk process orphan monitoring use case.

## Week 6 scope

- Integrate stage persistence and filesystem evidence outputs in end-to-end worker cycle.
- Execute shadow mode parity comparison in parallel with current flow.
- Compare functional parity by collection and persist parity evidence files.

## Week 7 scope

- Execute canary mode for a 10-20% platform subset.
- Apply tuning adjustments based on parity differences during shadow/canary cycles.
- Document operational runbook and rollback procedure for canary execution.