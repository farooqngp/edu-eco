# edu-eco Platform Architecture

Status: approved, phase 1 (repo scaffolding) implemented. Companion to `CLAUDE.md` (which
holds the condensed, agent-facing conventions) and `docs/SDLC_AUTOMATION.md` (the issue→PR
pipeline this architecture is built to be executed through). Individual decisions are
recorded as ADRs under `docs/architecture/ADR-*.md` as each one lands.

## Why this exists

`edu-eco` started as an empty scaffold with only an autonomous SDLC pipeline in place — no
application code, and `CLAUDE.md`'s conventions all marked `TBD`. That pipeline turns an
approved issue plan into a sequence of small, independently-reviewable PRs, but had nothing
concrete to build against. This document defines the target platform architecture — a
polyglot monorepo (.NET microservices + a Python agentic service behind a YARP gateway,
Angular frontend, SQL Server + Elasticsearch + Redis) on Clean Architecture per backend
service — precisely enough to hand to that pipeline as a sequence of phased issues.

## Locked decisions

- Monorepo orchestration: plain/native (no unifying meta-build-tool), except Nx scoped
  *only* to the Angular workspace (`src/frontend/web/`).
- API Gateway: YARP — the sole client-facing entry point; microservices are never called
  directly by the frontend.
- Backend: .NET microservices (Clean Architecture) for core domains + a Python "Agentic"
  service for AI/LLM-agent work.
- Databases: **SQL Server** (transactional, database-per-service) + Elasticsearch
  (non-transactional/search/read-model, always derived, never source of truth). Cache:
  Redis. Messaging: RabbitMQ + MassTransit (transactional outbox).
- Starter service set (renameable as real product domains land): Identity/Auth, `Catalog`
  (placeholder core-domain service), Notifications, plus the Python Agentic service.
- Error handling: `Result`/`Error` pattern for expected domain-rule violations; exceptions
  reserved for true infrastructure failures.

See `CLAUDE.md` for the condensed directory layout and exact build/test/lint commands the
SDLC implementation agent runs — this document carries the *rationale*, CLAUDE.md carries
the *literal instructions*.

## Backend: Clean Architecture per microservice

Four projects, one-way dependencies `Api → Infrastructure → Application → Domain`:

- **Domain** — zero external dependencies. Entities/aggregate roots, value objects, domain
  events, `Result`/`Error`. No EF Core, Redis, Elasticsearch, or MassTransit references.
- **Application** — MediatR commands/queries/handlers, FluentValidation validators, DTOs,
  and the *interfaces* Infrastructure implements (`IUserRepository`, `ICacheService`,
  `ISearchIndex<T>`, `IUnitOfWork`). Defines ports; never references infra packages.
- **Infrastructure** — EF Core `DbContext` + migrations (SQL Server), repository
  implementations, Elasticsearch adapters, Redis decorators, MassTransit consumers +
  transactional outbox wiring. The only project referencing infra packages.
- **Api** — composition root, minimal-API endpoints (bind → `mediator.Send` → map
  response). No business logic.

## BuildingBlocks — shared vs. never-shared

**Shared** (`src/shared/BuildingBlocks/*`):
- `Domain`: `Entity<TId>`, `AggregateRoot<TId>`, `ValueObject`, `IDomainEvent`, `Result`/`Error`.
- `Application`: MediatR pipeline behaviors (`ValidationBehavior`, `LoggingBehavior`,
  `UnitOfWorkBehavior` — commands don't call `SaveChanges` themselves), interface *shapes*
  for cache/lock/search/unit-of-work, `PagedResult<T>`.
- `Infrastructure`: outbox table conventions + `SaveChangesAsync` interceptor, MassTransit's
  built-in EF Core transactional outbox (`AddEntityFrameworkOutbox<TDbContext>` +
  `UseBusOutbox()` — not hand-rolled), `RedisCacheService`, `RedisDistributedLock`, bus
  configuration extension.

**Never shared** (the distributed-monolith trap): domain entities across services, EF
`DbContext`/entity configs beyond the base conventions, generic repository
implementations, or any compile-time `ProjectReference` between two services' Application
layers. `src/shared/contracts/dotnet/EduEco.Contracts.csproj` holds flat immutable record
DTOs only — never a domain entity, never a method, never a reference back into any service.

## Database-per-service + Elasticsearch sync

One logical SQL Server database per service (own connection string, own `DbContext`, own
migrations folder — no service ever holds another service's connection string).
Elasticsearch is always a derived read-model. Sync mechanism (no dual-write problem): a
command handler changes an aggregate → its domain events become integration events written
to an `OutboxMessages` row **in the same DB transaction** as the entity change → MassTransit's
transactional outbox delivers them to RabbitMQ at-least-once → an idempotent consumer
(upsert by aggregate ID) projects the event into the corresponding ES index. ES documents
are always rebuildable from SQL Server.

## Redis usage

| Pattern | Interface (Application) | Implementation (Infrastructure) |
|---|---|---|
| Cache-aside | `ICacheService.GetOrSetAsync<T>(key, factory, ttl)` | `RedisCacheService` |
| Distributed locks | `IDistributedLock.AcquireAsync(key, ttl)` | `RedisDistributedLock` |
| Token/session blacklist (Identity only) | `ITokenBlacklist` (Identity.Infrastructure only) | `RedisTokenBlacklist` |
| Gateway rate-limiting | n/a — gateway's own concern | shares the same Redis container; in-memory limiter today, Redis-backed limiter is future work for multi-instance |

## Inter-service communication

Default: async, event-driven, RabbitMQ + MassTransit, using the transactional outbox above.
RabbitMQ over Kafka (no high-throughput streaming/replay need at this scale) and over Azure
Service Bus (no confirmed cloud target — RabbitMQ runs identically in local docker-compose
and any cloud target). Sync calls only when justified: REST (client-facing, what YARP
proxies to) by default; gRPC reserved for latency-sensitive internal calls once a service is
stable enough to justify protobuf contracts. Integration event contracts live in
`src/shared/contracts/dotnet/EduEco.Contracts.csproj`, foldered `{Domain}/V{n}`
(additive-only within a version; breaking change = new `V{n+1}` folder).

## Python Agentic service

Hexagonal layout at `src/agentic/agentic_service/`: `domain/` (pydantic schemas, no
framework deps) → `application/` (use-cases/"agents" + ports as `Protocol` interfaces) →
`infrastructure/` (adapters: LLM provider, Elasticsearch-backed vector store, RabbitMQ
messaging via `aio-pika`, persistence) → `api/` (FastAPI composition root). Communicates
with .NET services via the gateway (REST, sync) and the same RabbitMQ broker (async
triggers) — message bodies are versioned JSON with a mirrored Pydantic model on the Python
side (intentional duplication across the language boundary, not a shared-domain-model
violation, since nothing is compiled/referenced cross-language). Dependency management:
`uv`. Tests: `pytest` + `pytest-asyncio` + `httpx`/FastAPI `TestClient`.

## YARP Gateway

Route config: one JSON file per downstream service under `Routes/*.routes.json`, merged
into the same `ReverseProxy` config section via `IConfiguration`'s key-merge behavior —
adding a new service means adding one new file, minimizing cross-phase-PR merge conflicts.
Middleware order: forwarded-headers → correlation-id → request logging → routing → CORS
(`AngularApp` policy) → JWT auth (validated locally against Identity's JWKS, cached, not a
synchronous call per request) → authorization → rate limiter → `MapReverseProxy` (forwards
`Authorization` header + correlation-id downstream unchanged). The gateway validates JWTs
for early rejection only — each downstream service still authorizes independently (defense
in depth).

## Angular frontend

One Angular workspace at `src/frontend/web/`, Nx-managed (the only place Nx is used in this
repo — justified by `nx affected` for scoped CI and module-boundary tags that structurally
enforce "features talk only to core/shared, never to each other"). Single shell app +
lazy-loaded feature libs (`feature-identity`, `feature-catalog`, `feature-notifications`,
`feature-agentic`) rather than multiple apps/micro-frontends. State: signals-based
(`signal()`/`computed()`, per-feature store services) by default; `@ngrx/signals`
(SignalStore) adopted per-feature only if that feature genuinely needs complex cross-feature
state sync. `libs/shared/data-access-contracts` wraps the generated
`src/shared/contracts/ts/*` clients so components never hand-type DTOs that can drift from
the real API.

## CI/CD

Three independent, path-filtered GitHub Actions workflows so a phase-scoped PR only builds
what it touched: `ci-dotnet.yml` (paths `src/services/**`, `src/gateway/**`,
`src/shared/BuildingBlocks/**`, `src/shared/contracts/dotnet/**` — a change under
`src/shared/**` must trigger every service's build+test, not just the literal changed
folder), `ci-python.yml` (`src/agentic/**`), `ci-angular.yml` (`src/frontend/web/**`, using
`nx affected` so only touched libs/apps run).

## Suggested phased rollout

1. Repo scaffolding (this document, `CLAUDE.md`, `global.json`, `Directory.Build.props`,
   `.editorconfig`, `docker-compose.yml`, `.gitignore` — **done**).
2. BuildingBlocks + Contracts shared projects.
3. Identity service (full 4-layer worked example, proves the pattern end-to-end).
4. Gateway wired to Identity only.
5. Angular shell + feature-identity.
6. CI workflows, verified green against phases 1–5.
7. Catalog service (second service, proves outbox → RabbitMQ → Elasticsearch projection).
8. Notifications service (consumes Catalog/Identity events, proves async integration).
9. Python Agentic service + gateway route + feature-agentic.
10. ADRs documenting the decisions actually implemented.

Each phase is sized to become one SDLC-pipeline issue, which the existing automation
further splits into per-phase PRs as needed.
