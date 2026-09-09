# Backend microservice layout

Referenced from `CLAUDE.md`. Applies to every service under `src/backend/Services/*`.
Clean Architecture, five projects per service, one-way dependencies
`Api → Infrastructure → Application → Domain`, `Contracts` referenced only by `Api` (and by
the gateway/TS-client generation, never by Domain/Application):

- **`{Service}.Domain`** — entities/aggregate roots (extend
  `BuildingBlocks.Domain.AggregateRoot<TId>`), value objects, domain events, `Result`/`Error`
  for expected failures. Zero references to Dapper, StackExchange.Redis, Elasticsearch,
  MassTransit, or ASP.NET Core — ever. Enforced by `tests/backend/Architecture`, not just
  this document.
- **`{Service}.Application`** — MediatR commands/queries/handlers, FluentValidation
  validators, DTOs, and the *interfaces* Infrastructure implements. Defines ports; never
  references infrastructure packages.
- **`{Service}.Infrastructure`** — Dapper Contrib repositories (simple single-table writes,
  via `BuildingBlocks.Infrastructure`'s `AuditableRepository<TEntity,TId>`), hand-written
  Dapper SQL for anything multi-table or read-side, DbUp-versioned migration scripts
  (PostgreSQL), Elasticsearch adapters, MassTransit consumers + outbox wiring (via
  `BuildingBlocks.Messaging`), Redis decorators (via `BuildingBlocks.Caching`). The only
  project allowed to reference infra packages. See `data-access.md` for the full
  Dapper/Dapper Contrib/DbUp/audit/soft-delete conventions.
- **`{Service}.Contracts`** — that service's own public request/response DTOs (records
  only).
- **`{Service}.Api`** — `Program.cs` composition root, minimal-API endpoints (bind →
  `mediator.Send` → map response using `{Service}.Contracts` DTOs). No business logic here.

**Error handling convention**: `Result`/`Error` (from `BuildingBlocks.Domain`) for expected
domain-rule violations returned to callers. Exceptions are reserved for true infrastructure
failures (DB unreachable, etc.) — do not use exceptions as control flow for validation.

**Never share across services**: domain entities, entity/table conventions beyond the base
conventions in `BuildingBlocks.Infrastructure`, generic repository implementations, a
`{Service}.Contracts` project, or any compile-time `ProjectReference` between two services'
Application layers. Cross-service data needs go through an integration event
(`src/shared/contracts/dotnet`) or, rarely, an explicit gRPC call — never a direct
reference.
