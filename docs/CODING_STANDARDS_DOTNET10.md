# Coding Standards - .NET 10, Modular Hexagonal (Clean Architecture) + TDD

## 1) Scope

This standard applies to all .NET 10 modules, new or existing, including refactors.

Goals:

- Keep business rules independent from frameworks and infrastructure.
- Enforce testability first through TDD.
- Reduce coupling and keep modules evolvable.
- Make behavior explicit through use cases and contracts.

## 2) Architecture Baseline

Use Modular Monolith with Hexagonal boundaries.

Required layers per module:

- Domain: entities, value objects, domain services, domain events, invariants.
- Application: use cases, command/query handlers, ports (interfaces), transaction boundaries.
- Infrastructure: adapters (database, messaging, file storage, external APIs, ffmpeg wrappers).
- Presentation: API endpoints, DTO mapping, auth policies, validation, serialization.

Dependency rule:

- Domain depends on nothing.
- Application depends on Domain.
- Infrastructure depends on Application and Domain.
- Presentation depends on Application.
- No other direction is allowed.

## 3) Module Design Rules

Each business capability is a module. Example: Capture, Segmentation, Alerts, Reports, Evidence, Tenancy.

For each module, define:

- Public application contracts (commands, queries, results).
- Inbound ports (application interfaces used by presentation/workers).
- Outbound ports (interfaces for DB, queues, storage, external tools).
- Infrastructure adapters implementing outbound ports.

Do not:

- Access database directly from controllers/endpoints.
- Put business rules inside adapters.
- Share mutable state between modules.
- Reference Infrastructure from Domain.

## 4) Solution and Project Layout

Recommended structure:

- src/
  - BuildingBlocks/
    - Domain/
    - Application/
    - Infrastructure/
    - Testing/
  - Modules/
    - Capture/
      - Capture.Domain/
      - Capture.Application/
      - Capture.Infrastructure/
      - Capture.Api/ (optional if module owns endpoints)
    - Segmentation/
    - Alerts/
    - Reports/
  - Api/
    - Host.Api/
  - Workers/
    - Ingestion.Worker/
    - Processing.Worker/
- tests/
  - Unit/
  - Integration/
  - Contract/
  - Architecture/

## 5) Coding Conventions

Language/runtime:

- C# latest compatible with .NET 10.
- Nullable enabled.
- Implicit usings enabled.
- Treat warnings as errors in CI.

Style:

- One class per file.
- Explicit names over abbreviations.
- Prefer immutable records/value objects when possible.
- Avoid static mutable state.
- Keep methods small and single-purpose.

Error handling:

- Use typed results/exceptions at module boundaries.
- Do not leak infrastructure exceptions to presentation.
- Map errors to explicit API responses.

Observability:

- Structured logging only.
- Correlation/trace id propagated across API and workers.
- Emit key business metrics per use case.

## 6) API Standards

- Contract-first DTOs (request/response models versioned).
- Validate input at edge.
- Keep controllers/endpoints thin; call use cases only.
- OpenAPI required for every exposed endpoint.
- Backward compatibility policy for public contracts.

## 7) Data and Persistence Standards

- Repositories are outbound ports in Application.
- DB models/map configurations stay in Infrastructure.
- Migrations scripted and versioned.
- Add indexes based on access patterns, not guesses.
- Multi-tenant boundaries must be explicit in keys/queries.

## 8) Worker Standards

- Worker host only orchestrates use cases.
- Long-running loops must support cancellation tokens.
- Retry with backoff for transient faults.
- Idempotency required for message/event reprocessing.
- Dead-letter strategy required for failed messages.

## 9) TDD Standard (Mandatory for new behavior)

Workflow:

1. Red: write a failing test that expresses behavior.
2. Green: implement minimal code to pass.
3. Refactor: improve design while tests stay green.

Test granularity:

- Unit tests: domain and application rules (fast, isolated).
- Integration tests: adapters (DB, queue, storage, external process wrappers).
- Contract tests: API contracts and external integrations.
- Architecture tests: enforce dependency rules and layering.

Minimum quality gates:

- New business logic must include unit tests.
- Critical flows (alerts, evidence, segmentation) require integration tests.
- CI blocks merge on failing tests.

## 10) Definition of Done (DoD)

A task is done only if:

- Requirement mapped to module/use case.
- TDD cycle executed (red-green-refactor evidence in commit history).
- Tests pass locally and in CI.
- Logging/metrics added for operational visibility.
- Documentation updated (contract, behavior, assumptions).
- No architecture rule violations.

## 11) Pull Request Checklist

- What requirement does this change satisfy?
- Which module/use case was changed?
- Which tests were added/updated?
- Any migration/index/config change included?
- Any backward compatibility impact?
- Any operational impact (alerts, metrics, retries, throughput)?
- Are identifiers and in-code technical documentation written in English?

## 12) Refactoring Existing Code

- Do not big-bang rewrite unless approved.
- Add characterization tests before refactoring uncertain behavior.

## 13) Non-Negotiable Rules

- No business logic in controllers, jobs, or repositories.
- No direct infrastructure dependency inside Domain.
- No merge without tests for new/changed business behavior.
- No silent catch blocks.
- No TODO in critical flow without linked backlog item.

## 14) Language Policy (Mandatory)

For `media-mentions-monitoring`, all code and code-adjacent technical documentation must be written in English.

This includes:

- Class, interface, type, enum, function, method, variable, constant, and property names.
- DTO/entity/model names and field/property names.
- Inline comments, docstrings, JSDoc/TSDoc, and code examples.
- Developer/operator-facing technical messages.

Allowed exceptions:

- User-facing text intentionally in Spanish (UI labels, report content, monitored content).
- Proper nouns and legal/contract terms that must remain in original language.

Enforcement:

- Do not introduce new Spanish identifiers or in-code technical documentation.
- If touching existing Spanish identifiers, use English for new code and rename them when safe.
