# edu-eco Platform Architecture

Status: approved, phase 1 (repo scaffolding) implemented; this revision reconciles that
baseline with a more detailed reference structure the user supplied
(`mono-repo-structure.md`) — BuildingBlocks split into 7 focused projects, EF Core+Dapper,
per-service `Contracts`, top-level `tests/` with Architecture fitness tests, and a
multi-agent Python system. No `src/` exists yet, so this is a pure docs/convention update,
not a migration. Companion to `CLAUDE.md` (condensed, agent-facing conventions) and
`docs/SDLC_AUTOMATION.md` (the issue→PR pipeline this architecture is executed through).
Individual decisions are recorded as ADRs under `docs/architecture/ADR-*.md` as each lands.

## Why this exists

`edu-eco` started as an empty scaffold with only an autonomous SDLC pipeline in place — no
application code, and `CLAUDE.md`'s conventions all marked `TBD`. That pipeline turns an
approved issue plan into a sequence of small, independently-reviewable PRs, but had nothing
concrete to build against. This document defines the target platform architecture — a
polyglot monorepo (.NET microservices + a Python multi-agent AI system behind a YARP
gateway, Angular frontend, SQL Server + Elasticsearch + Redis) on Clean Architecture per
backend service — precisely enough to hand to that pipeline as a sequence of phased issues.

## Locked decisions

- Monorepo orchestration: plain/native (no unifying meta-build-tool), except Nx scoped
  *only* to the Angular workspace (`src/frontend/web/`).
- API Gateway: YARP (`src/backend/ApiGateway/`) — the sole client-facing entry point;
  microservices are never called directly by the frontend.
- Backend: .NET microservices (Clean Architecture, 5 projects each — see below) for core
  domains + a Python multi-agent AI system for LLM-agent work.
- Databases: **SQL Server** (transactional, database-per-service; EF Core for writes,
  Dapper for reads — see "Data access") + Elasticsearch (non-transactional/search/read-model,
  always derived, never source of truth). Cache: Redis. Messaging: RabbitMQ + MassTransit
  (transactional outbox).
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
- **Infrastructure** — EF Core `DbContext` + migrations (SQL Server, command/write side),
  Dapper read repositories (query side), Elasticsearch adapters, MassTransit consumers +
  outbox wiring (via `BuildingBlocks.Messaging`), Redis decorators (via
  `BuildingBlocks.Caching`). The only project referencing infra packages directly.
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
- **`BuildingBlocks.Infrastructure`** — outbox model-builder conventions +
  `SaveChangesInterceptor` that dispatches pending domain events via MediatR before the
  physical save; Dapper read-repository base helpers. References
  `BuildingBlocks.Application`.
- **`BuildingBlocks.Messaging`** — MassTransit + RabbitMQ configuration, MassTransit's
  built-in EF Core transactional outbox (`AddEntityFrameworkOutbox<TDbContext>` +
  `UseBusOutbox()` — not hand-rolled). References `BuildingBlocks.Application`.
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

## Data access: EF Core + Dapper

EF Core owns the write side exclusively — every command goes through the `DbContext`,
aggregate, and `SaveChangesAsync`, which is what makes the domain-event-dispatch
interceptor and the transactional outbox work at all. Dapper owns the read side: queries
that don't need change-tracking, or that benefit from hand-tuned SQL (lists, search-style
filters, reports), go through a Dapper-based read repository implementing the same
Application-layer query port, using the *same* connection string/database as the service's
own `DbContext` — never a separate database. Dapper is never used to write; that would let
state changes bypass the outbox and silently break the Elasticsearch-sync guarantee below.

## Database-per-service + Elasticsearch sync

One logical SQL Server database per service (own connection string, own `DbContext`, own
migrations folder — no service ever holds another service's connection string).
Elasticsearch is always a derived read-model. Sync mechanism (no dual-write problem): a
command handler changes an aggregate (via EF Core) → its domain events become integration
events written to an outbox row **in the same DB transaction** as the entity change →
MassTransit's transactional outbox delivers them to RabbitMQ at-least-once → an idempotent
consumer (upsert by aggregate ID) projects the event into the corresponding ES index. ES
documents are always rebuildable from SQL Server.

## Redis usage

| Pattern | Interface (Application) | Implementation (`BuildingBlocks.Caching`) |
|---|---|---|
| Cache-aside | `ICacheService.GetOrSetAsync<T>(key, factory, ttl)` | `RedisCacheService` |
| Distributed locks | `IDistributedLock.AcquireAsync(key, ttl)` | `RedisDistributedLock` |
| Token/session blacklist (Identity only) | `ITokenBlacklist` (Identity.Infrastructure only) | `RedisTokenBlacklist` |
| Gateway rate-limiting | n/a — gateway's own concern | shares the same Redis container; in-memory limiter today, Redis-backed limiter is future work for multi-instance |

## Inter-service communication

Default: async, event-driven, RabbitMQ + MassTransit (`BuildingBlocks.Messaging`), using the
transactional outbox above. RabbitMQ over Kafka (no high-throughput streaming/replay need at
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
- **`tests/backend/Integration/<Service>`** — real SQL Server/Redis/Elasticsearch/RabbitMQ
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

## CI/CD

Independent, path-filtered GitHub Actions workflows so a phase-scoped PR only builds what it
touched: `ci-dotnet.yml` (paths `src/backend/**`, `src/shared/contracts/dotnet/**`,
`tests/backend/**` — a change under `src/backend/BuildingBlocks/**` or
`src/shared/contracts/dotnet/**` must trigger every service's build+test, not just the
literal changed folder; the Architecture fitness-test project always runs), `ci-python.yml`
(`src/agents/**`, `tests/agents/**`), `ci-angular.yml` (`src/frontend/web/**`,
`tests/frontend/**`, using `nx affected` so only touched libs/apps run).

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
   proves the pattern end-to-end, including the EF Core write-side + Dapper read-side split).
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
