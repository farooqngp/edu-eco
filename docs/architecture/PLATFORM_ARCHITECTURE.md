# edu-eco Platform Architecture

Status: approved, phase 1 (repo scaffolding) implemented; BuildingBlocks (7 focused
projects) scaffolded as empty `.csproj` shells, zero `.cs` files written yet. This revision
reverses the earlier EF Core (writes) + Dapper (reads) split to **Dapper + Dapper Contrib
only, no EF Core anywhere in this repo** — a deliberate architectural decision, made before
any BuildingBlocks code was written, driven by wanting explicit hand-tuned SQL and no
ORM change-tracking magic across the whole stack, not just the read side. This revision
also adds conventions for table/column documentation, audit columns, soft delete, and
audit-trail logging that apply repo-wide once any table is created. Since no BuildingBlocks
`.cs` files or services exist yet, this is a pure docs/convention update, not a code
migration. Companion to `CLAUDE.md` (condensed, agent-facing conventions, now split into
`docs/conventions/*.md` by topic) and `docs/SDLC_AUTOMATION.md` (the issue→PR pipeline this
architecture is executed through). Individual decisions are recorded as ADRs under
`docs/architecture/ADR-*.md` as each lands.

## Why this exists

`edu-eco` started as an empty scaffold with only an autonomous SDLC pipeline in place — no
application code, and `CLAUDE.md`'s conventions all marked `TBD`. That pipeline turns an
approved issue plan into a sequence of small, independently-reviewable PRs, but had nothing
concrete to build against. This document defines the target platform architecture — a
polyglot monorepo (.NET microservices + a Python multi-agent AI system behind a YARP
gateway, Angular frontend, PostgreSQL + Elasticsearch + Redis) on Clean Architecture per
backend service — precisely enough to hand to that pipeline as a sequence of phased issues.

## Locked decisions

- Monorepo orchestration: plain/native (no unifying meta-build-tool), except Nx scoped
  *only* to the Angular workspace (`src/frontend/web/`).
- API Gateway: YARP (`src/backend/ApiGateway/`) — the sole client-facing entry point;
  microservices are never called directly by the frontend.
- Backend: .NET microservices (Clean Architecture, 5 projects each — see below) for core
  domains + a Python multi-agent AI system for LLM-agent work.
- Databases: **PostgreSQL** (transactional, database-per-service; Dapper + Dapper Contrib
  for all data access, writes included — no EF Core anywhere, see "Data access") +
  Elasticsearch (non-transactional/search/read-model, always derived, never source of
  truth). Cache: Redis. Messaging: RabbitMQ + MassTransit with a hand-rolled transactional
  outbox (MassTransit's built-in outbox only supports EF Core/MongoDB, not Dapper —
  [confirmed](https://github.com/MassTransit/MassTransit/discussions/5600) — see
  "Inter-service communication").
- Starter service set (renameable as real product domains land): `Identity`, `Customer`,
  `Order`, `Notification`, `Search` — plus the Python multi-agent system.
- Error handling: `Result`/`Error` pattern for expected domain-rule violations; exceptions
  reserved for true infrastructure failures.
- Tests live top-level under `tests/`, grouped by type (Unit/Integration/Contract/
  Architecture/E2E), not colocated per project — see "Testing strategy".

See `CLAUDE.md` for the condensed directory layout and exact build/test/lint commands the
SDLC implementation agent runs — this document carries the *rationale*, CLAUDE.md carries
the *literal instructions*.

## Backend: Clean Architecture per microservice

Five projects, one-way dependencies `Api → Infrastructure → Application → Domain`;
`Contracts` sits beside them and is referenced only by `Api` and by external callers
(gateway, generated TS clients) — never by another service:

- **Domain** — zero external dependencies. Entities/aggregate roots, value objects, domain
  events, `Result`/`Error`. No EF Core, Dapper, Redis, Elasticsearch, MassTransit, or
  ASP.NET Core references. Enforced by `tests/backend/Architecture` (NetArchTest), not just
  documented here.
- **Application** — MediatR commands/queries/handlers, FluentValidation validators, DTOs,
  and the *interfaces* Infrastructure implements (`IUserRepository`, `IUserReadRepository`,
  `ICacheService`, `ISearchIndex<T>`, `IUnitOfWork`, `ICurrentUser`). Defines ports; never
  references infra packages.
- **Infrastructure** — Dapper Contrib repositories for simple single-table writes (via the
  shared `AuditableRepository<TEntity,TId>` base — see "Data access"), hand-written Dapper
  SQL for anything multi-table or read-side, DbUp-versioned `.sql` migration scripts
  (PostgreSQL), Elasticsearch adapters, MassTransit consumers + the hand-rolled outbox relay
  (via `BuildingBlocks.Messaging`), Redis decorators (via `BuildingBlocks.Caching`). The
  only project referencing infra packages directly.
- **Contracts** — that service's own public REST request/response DTOs (records only). Not
  to be confused with `src/shared/contracts/dotnet` (`EduEco.Contracts`, cross-service
  integration events) — see "Two kinds of Contracts" below.
- **Api** — composition root, minimal-API endpoints (bind → `mediator.Send` → map response
  using this service's own `Contracts` DTOs). No business logic.

## Two kinds of Contracts

A genuine, deliberate distinction — not duplication:

- **`{Service}.Contracts`** (per service) answers "what does this service's HTTP API look
  like to a caller" — owned entirely by that service, evolves with its endpoints, referenced
  by the gateway/TS-client generation for that service only.
- **`EduEco.Contracts`** (`src/shared/contracts/dotnet`, one project, shared) answers "what
  facts does this service broadcast to the rest of the system" — cross-service integration
  events over RabbitMQ, foldered `{Domain}/V{n}`, flat immutable records only, zero methods,
  zero references back into any service. This is the one deliberate exception to "never
  share code between services," precisely because the alternative (every consumer
  hand-rolling its own copy of the event shape) is worse.

Neither project ever becomes a place to share domain logic — both hold data shapes only.

## BuildingBlocks — 7 focused projects, shared vs. never-shared

Split (not one grab-bag `Infrastructure` project) so a service only pulls in what it
actually uses — a service with no messaging needs shouldn't transitively reference
MassTransit:

- **`BuildingBlocks.Domain`** — `Entity<TId>`, `AggregateRoot<TId>`, `ValueObject`,
  `IDomainEvent`, `Result`/`Error`. Zero package references.
- **`BuildingBlocks.Application`** — MediatR pipeline behaviors (`ValidationBehavior`,
  `LoggingBehavior`, `UnitOfWorkBehavior` — commands don't call `SaveChanges` themselves),
  port *interface shapes* for cache/lock/search/unit-of-work/current-user, `PagedResult<T>`.
  References only `BuildingBlocks.Domain`.
- **`BuildingBlocks.Infrastructure`** — Dapper/Dapper Contrib base repository conventions
  (`AuditableRepository<TEntity,TId>` — stamps audit columns, turns deletes into soft-delete
  updates, writes the audit-log row, all inside one `IDbTransaction`), the DbUp migration
  runner, and the soft-delete `v_{table}` view convention. References
  `BuildingBlocks.Application`. No reference to MassTransit or EF Core — it has no outbox
  knowledge at all, and no ORM change-tracking.
- **`BuildingBlocks.Messaging`** — MassTransit + RabbitMQ configuration, plus a hand-rolled
  transactional outbox: an `outbox_messages` table written via the *same* Dapper
  `IDbTransaction` as the business write (atomicity is free — no two-phase commit needed),
  and an `OutboxRelayService : BackgroundService` that polls unpublished rows, calls
  `IBus.Publish`, and marks them dispatched. Hand-rolled because MassTransit's built-in
  transactional outbox only supports EF Core and MongoDB, not Dapper
  ([confirmed](https://github.com/MassTransit/MassTransit/discussions/5600)). References
  `BuildingBlocks.Application`. Infrastructure and Messaging never reference each other — a
  service's own composition root (`Program.cs`) wires both.
- **`BuildingBlocks.Caching`** — `RedisCacheService`, `RedisDistributedLock` (hand-rolled
  `SET key token NX PX ttl` + Lua compare-and-delete release — not RedLock.net, since a
  single Redis instance per `docker-compose.yml` has no multi-node split-brain concern to
  solve). References `BuildingBlocks.Application`.
- **`BuildingBlocks.Observability`** — structured logging (Serilog enrichers),
  correlation-id propagation, a shared OpenTelemetry tracing/metrics setup extension used by
  every service and the gateway.
- **`BuildingBlocks.Security`** — `ICurrentUser`'s concrete implementation (HttpContext
  claims), shared authorization policy names/constants, JWT helpers reused by both the
  gateway (early validation) and each service (defense-in-depth re-authorization).

**Never shared** (the distributed-monolith trap): domain entities across services, EF
`DbContext`/entity configs beyond the base conventions, generic repository
implementations, a `{Service}.Contracts` project, or any compile-time `ProjectReference`
between two services' Application layers.

## Data access: Dapper + Dapper Contrib (no EF Core, anywhere)

No EF Core reference exists anywhere in this repo, on the read or write side. Dapper
Contrib's `Insert`/`Update`/`Get`/`GetAll` extension methods handle simple single-table
writes (wrapped by the shared `AuditableRepository<TEntity,TId>` base in
`BuildingBlocks.Infrastructure` — see below); hand-written SQL via plain Dapper handles
anything multi-table, batch, or read-side (list/search/report-style queries). A Dapper
read-repository implements the same Application-layer query port (`IOrderReadRepository`,
etc.) using the *same* connection string as the service's writes — never a separate
database.

**Unit of work without change-tracking**: `IUnitOfWork` (`BuildingBlocks.Application`)
wraps a single `IDbConnection`/`IDbTransaction` pair per request. Repositories take the
ambient transaction rather than owning their own connection, so every write in one command
handler commits atomically. `UnitOfWorkBehavior` (MediatR pipeline) opens the transaction
before the handler runs and commits it after, only if the handler's `Result` is successful
— same contract as before, different mechanism underneath.

**Domain event dispatch without an interceptor**: repositories register the aggregates
they touch on the ambient `IUnitOfWork` (e.g. `unitOfWork.TrackAggregate(entity)` on
Insert/Update). After `UnitOfWorkBehavior` commits the transaction, it walks the tracked
aggregates and dispatches their `DomainEvents` via MediatR — explicit rather than the
implicit dispatch EF's `SavingChangesAsync` interceptor previously did, which fits Dapper's
no-hidden-behavior philosophy better anyway.

**Migrations**: [DbUp](https://dbup.readthedocs.io/), not EF Core migrations — plain
versioned `.sql` scripts, checksummed and applied in order, no code-gen. Scripts live under
`{Service}.Infrastructure/Migrations/NNNN_description.sql`, applied via a small
`MigrationRunner` in `BuildingBlocks.Infrastructure` wrapping
`DeployChanges.To.PostgresqlDatabase(...).WithScriptsEmbeddedInAssembly(...)`. Auto-run
only when `ASPNETCORE_ENVIRONMENT == Development` — same gate as before, different tool.

**Table/column documentation**: every `CREATE TABLE` migration script includes
`COMMENT ON TABLE ...` / `COMMENT ON COLUMN ...` statements by hand, in the same script —
real PostgreSQL catalog metadata (queryable via `information_schema` +
`pg_catalog.col_description()`), the same RAG-ready target the EF `.HasComment()` approach
was aiming for, just authored directly in SQL instead of generated from C# attributes.
Enforcement: an integration test (Testcontainers Postgres) runs a service's migration
scripts, then asserts every table/column has a non-null comment — fails the build if any
table ships undocumented.

**Audit columns + soft delete**: `IAuditableEntity`/`ISoftDeletableEntity` are implemented
directly by `BuildingBlocks.Domain`'s `Entity<TId>` — every table-backed entity gets both,
with zero opt-in code required anywhere. `AuditableRepository<TEntity,TId>` stamps
`CreatedAtUtc`/`CreatedBy` or `LastModifiedAtUtc`/`LastModifiedBy` before every
Insert/Update, and turns "delete" into `UPDATE ... SET is_deleted = true, deleted_at_utc =
..., deleted_by = ...` — no physical `DELETE` is ever issued. Soft-delete filtering is
convention plus a generated view, not automatic: DbUp creates `v_{table}` (`SELECT * FROM
{table} WHERE is_deleted = false`) for every soft-deletable table; read-repositories query
the view by default, and only an explicitly named `GetIncludingDeletedAsync` queries the
base table directly. (This is the one place raw Dapper genuinely loses something EF's
global `HasQueryFilter` gave for free — a filter no query could forget — the view is the
closest substitute, but it's convention-enforced, not compiler-enforced.)

**Audit-trail logging**: `AuditableRepository<TEntity,TId>` writes an `AuditLogEntry` row
(`EntityName`, `EntityId`, `Action`, `ChangedAtUtc`, `ChangedBy`, `OldValues`/`NewValues` as
JSON text, `CorrelationId` from `Activity.Current?.Id`) inside the *same* `IDbTransaction`
as the business write — atomicity is free, so the audit trail can never drift out of sync
with what actually happened.

## Database-per-service + Elasticsearch sync

One logical PostgreSQL database per service (own connection string, own `DbContext`, own
migrations folder — no service ever holds another service's connection string).
Elasticsearch is always a derived read-model. Sync mechanism (no dual-write problem): a command handler changes an aggregate (via a
Dapper Contrib repository) → its domain events become integration events written to the
hand-rolled `outbox_messages` table **in the same DB transaction** as the entity change
(see "Data access") → the `OutboxRelayService` background poller delivers them to RabbitMQ
at-least-once → an idempotent consumer (upsert by aggregate ID) projects the event into the
corresponding ES index. ES documents are always rebuildable from PostgreSQL.

## Redis usage

| Pattern | Interface (Application) | Implementation (`BuildingBlocks.Caching`) |
|---|---|---|
| Cache-aside | `ICacheService.GetOrSetAsync<T>(key, factory, ttl)` | `RedisCacheService` |
| Distributed locks | `IDistributedLock.AcquireAsync(key, ttl)` | `RedisDistributedLock` |
| Token/session blacklist (Identity only) | `ITokenBlacklist` (Identity.Infrastructure only) | `RedisTokenBlacklist` |
| Gateway rate-limiting | n/a — gateway's own concern | shares the same Redis container; in-memory limiter today, Redis-backed limiter is future work for multi-instance |

## Inter-service communication

Default: async, event-driven, RabbitMQ + MassTransit (`BuildingBlocks.Messaging`), using the
hand-rolled transactional outbox above. RabbitMQ over Kafka (no high-throughput streaming/replay need at
this scale) and over Azure Service Bus (no confirmed cloud target — RabbitMQ runs identically
in local docker-compose and any cloud target). Sync calls only when justified: REST
(client-facing, what YARP proxies to) by default; gRPC reserved for latency-sensitive
internal calls once a service is stable enough to justify protobuf contracts. Integration
event contracts live in `src/shared/contracts/dotnet/EduEco.Contracts.csproj`, foldered
`{Domain}/V{n}` (additive-only within a version; breaking change = new `V{n+1}` folder).

## Python multi-agent AI system

`src/agents/` — a role-based multi-agent system, not a single monolithic service:

- `agents/planner/`, `agents/researcher/`, `agents/executor/`, `agents/reviewer/` — each a
  focused agent role with its own prompt/behavior, following the same
  domain→application→infrastructure layering internally (pydantic schemas with no framework
  deps → use-case/agent logic + ports as `Protocol` interfaces → adapters for LLM
  provider/vector store/tools).
- `tools/` — concrete tool implementations agents can invoke (e.g. a search tool backed by
  Elasticsearch, a "call service X" tool that goes through the gateway).
- `workflows/` — multi-agent orchestration graphs (which agents run in what order/branching
  for a given task type) — the layer that actually wires planner→researcher→executor→
  reviewer together for a given workflow.
- `shared/` — reusable Python components (config loading, logging setup, common base
  classes) used across agents/tools/workflows.
- `api/` — FastAPI composition root exposing the multi-agent system to the rest of the
  platform.

Communicates with .NET services via the gateway (REST, sync) and the same RabbitMQ broker
(async triggers) — message bodies are versioned JSON with a mirrored Pydantic model on the
Python side (intentional duplication across the language boundary, not a shared-domain-model
violation, since nothing is compiled/referenced cross-language). Dependency management: `uv`.
Tests: `pytest` + `pytest-asyncio` + `httpx`/FastAPI `TestClient`, under `tests/agents/`.

## YARP Gateway

`src/backend/ApiGateway/`. Route config: one JSON file per downstream service under
`Routes/*.routes.json`, merged into the same `ReverseProxy` config section via
`IConfiguration`'s key-merge behavior — adding a new service means adding one new file,
minimizing cross-phase-PR merge conflicts. Middleware order: forwarded-headers →
correlation-id → request logging → routing → CORS (`AngularApp` policy) → JWT auth
(validated locally against Identity's JWKS, cached, not a synchronous call per request,
using `BuildingBlocks.Security` helpers) → authorization → rate limiter → `MapReverseProxy`
(forwards `Authorization` header + correlation-id downstream unchanged). The gateway
validates JWTs for early rejection only — each downstream service still authorizes
independently (defense in depth).

## Angular frontend

One Angular workspace at `src/frontend/web/`, Nx-managed (the only place Nx is used in this
repo — justified by `nx affected` for scoped CI and module-boundary tags that structurally
enforce "features talk only to core/shared, never to each other"). Single shell app +
lazy-loaded feature libs (`feature-identity`, `feature-customer`, `feature-order`,
`feature-notification`, `feature-search`, `feature-agents`) rather than multiple apps/
micro-frontends. State: signals-based (`signal()`/`computed()`, per-feature store services)
by default; `@ngrx/signals` (SignalStore) adopted per-feature only if that feature genuinely
needs complex cross-feature state sync. `libs/shared/data-access-contracts` wraps the
generated `src/shared/contracts/ts/*` clients (one per service, generated from each
service's own `Contracts`/OpenAPI spec) so components never hand-type DTOs that can drift
from the real API.

## Testing strategy

`tests/` is top-level, grouped by test type rather than colocated per project:

- **`tests/backend/Unit/<Service>`** — Domain/Application logic in isolation, no real infra.
- **`tests/backend/Integration/<Service>`** — real PostgreSQL/Redis/Elasticsearch/RabbitMQ
  via Testcontainers; exercises the actual `Infrastructure` implementations.
- **`tests/backend/Contract`** — API/event contract tests between services (e.g. a
  published integration event's schema matches what `EduEco.Contracts` declares).
- **`tests/backend/Architecture`** — NetArchTest fitness tests that *assert* the Clean
  Architecture dependency rules above (no `*.Domain` type may reference EF Core/Dapper/
  Redis/MassTransit/ASP.NET Core; no service Application layer may reference another
  service; etc.) as an actual failing test, not just documentation. Runs on every backend PR
  regardless of which project changed.
- **`tests/agents/`** — pytest suites for the Python multi-agent system.
- **`tests/frontend/`** — Angular unit specs + e2e for critical user journeys.

Rationale for top-level-by-type over colocated `{Project}.Tests`: the Architecture project
specifically needs to see every backend assembly at once to assert cross-cutting rules, and
grouping by type makes each category's infrastructure needs obvious (Integration needs
`docker compose up`, Unit/Architecture don't).

**Coverage gate**: `Domain` and `Application` projects (every service's, and BuildingBlocks')
require 95% line coverage, enforced via `coverlet.msbuild`'s `/p:Threshold` — see CLAUDE.md's
"Testing rules (code coverage)" for the exact command. Scoped deliberately to just these two
layers: they hold the actual business logic and are cheap to unit-test in isolation, whereas
`Infrastructure`/`Api`/`Contracts` (and the infra-flavored BuildingBlocks projects) are thin
adapters/wiring better proven by `tests/backend/Integration` against real dependencies than
by a line-coverage percentage — a 95% floor on `Program.cs` would just reward padding, not
confidence.

## CI/CD

Independent, path-filtered GitHub Actions workflows so a phase-scoped PR only builds what it
touched: `ci-dotnet.yml` (paths `edueco/BuildingBlocks/**`, `src/backend/**`,
`src/shared/contracts/dotnet/**`, `tests/backend/**` — a change under
`edueco/BuildingBlocks/**` or `src/shared/contracts/dotnet/**` must trigger every service's
build+test, not just the literal changed folder; the Architecture fitness-test project
always runs), `ci-python.yml` (`src/agents/**`, `tests/agents/**`), `ci-angular.yml`
(`src/frontend/web/**`, `tests/frontend/**`, using `nx affected` so only touched libs/apps
run).

## Suggested phased rollout

1. Repo scaffolding (this document, `CLAUDE.md`, `global.json`, `Directory.Build.props`,
   `Directory.Packages.props`, `.editorconfig`, `docker-compose.yml`, `Makefile`,
   `.gitignore` — **done**, this revision reconciles it with the 7-project BuildingBlocks
   split/Dapper/multi-agent structure below).
2. BuildingBlocks (7 projects) + `EduEco.Contracts` + the Architecture fitness-test project.
   *(Supersedes the earlier draft issue body prepared before this reconciliation — that
   draft assumed a 3-project consolidated BuildingBlocks and must be regenerated against
   this structure before filing.)*
3. Identity service (full 5-project worked example incl. its own `Identity.Contracts`,
   proves the pattern end-to-end, including the Dapper Contrib write-side + hand-written
   Dapper read-side split, the DbUp migrations, and the hand-rolled outbox relay).
4. Gateway wired to Identity only.
5. Angular shell + feature-identity.
6. CI workflows, verified green against phases 1–5.
7. Customer service (second service, proves outbox → RabbitMQ → Elasticsearch projection).
8. Order + Notification services (consume Customer/Identity events, prove async
   integration across three services).
9. Search service (Elasticsearch-facing read/query service).
10. Python multi-agent system (planner/researcher/executor/reviewer + workflows) + gateway
    route + feature-agents.
11. ADRs documenting the decisions actually implemented.

Each phase is sized to become one SDLC-pipeline issue, which the existing automation
further splits into per-phase PRs as needed.
