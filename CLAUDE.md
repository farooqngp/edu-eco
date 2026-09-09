# CLAUDE.md

Guidance for any agent (planning or coding) working in this repo. Read this fully before
drafting a plan or writing code. Keep this file current as the codebase grows — the
autonomous SDLC workflows in `.github/workflows/sdlc-*.yml` key their behavior off it.

## Project

`edu-eco` — a polyglot monorepo. Domain/product specifics land here once the first
real features are built; for now this section documents the platform architecture that
every service is built on. Full design rationale: `docs/architecture/PLATFORM_ARCHITECTURE.md`
(see also the individual ADRs under `docs/architecture/`).

## Conventions

### Language / stack

- **.NET 10** (C#) — microservices under `src/backend/Services/*`, the API gateway under
  `src/backend/ApiGateway/`, and shared libraries under `edueco/BuildingBlocks/*`. Each
  microservice follows Clean Architecture (see "Backend microservice layout" below).
- **Python 3.13** (`uv`, FastAPI) — the multi-agent AI system under `src/agents/`
  (planner/researcher/executor/reviewer roles + shared tools/workflow orchestration).
- **Angular** (latest, Nx-managed workspace) — frontend under `src/frontend/web/`.
- **PostgreSQL** — transactional data, one logical database per microservice (never
  shared across services). Writes go through EF Core; reads that don't need change-tracking
  or need to be fast go through Dapper (see "Data access: EF Core + Dapper" below).
- **Elasticsearch** — non-transactional/search/read-model data only. Never the source of
  truth; always populated asynchronously from the transactional side via the outbox
  pattern (see below). Safe to fully rebuild from PostgreSQL at any time.
- **Redis** — cache-aside, distributed locks, Identity's token/session blacklist.
- **RabbitMQ + MassTransit** — inter-service integration events, using MassTransit's
  EF Core transactional outbox (not a hand-rolled poller).
- **YARP** — the single API gateway. The Angular app and any external client call only
  the gateway; microservices are never called directly.

### Directory layout

```
edueco/
  BuildingBlocks/            # 7 focused shared libraries — see "BuildingBlocks" below
    BuildingBlocks.Domain/
    BuildingBlocks.Application/
    BuildingBlocks.Infrastructure/     # EF Core + Dapper base conventions only
    BuildingBlocks.Observability/      # logging/tracing/metrics conventions
    BuildingBlocks.Messaging/          # MassTransit/RabbitMQ + outbox
    BuildingBlocks.Caching/            # Redis cache + distributed lock
    BuildingBlocks.Security/           # shared auth/claims/policy helpers
src/
  backend/
    ApiGateway/              # YARP reverse proxy — ApiGateway.Api, ApiGateway.Api.Tests live under tests/
    Services/
      Identity/              # Identity.{Api,Application,Domain,Infrastructure,Contracts}
      Customer/               # placeholder core-domain service — same pattern, rename when the real domain lands
      Order/                  # same pattern
      Notification/           # same pattern
      Search/                 # same pattern
  agents/                    # Python multi-agent AI system
    shared/                  # reusable Python components (config, logging, base classes)
    agents/
      planner/
      researcher/
      executor/
      reviewer/
    tools/                   # agent tool implementations
    workflows/               # multi-agent orchestration graphs
    api/                     # FastAPI entry point
  shared/
    contracts/
      dotnet/                # EduEco.Contracts.csproj — cross-service INTEGRATION EVENTS only,
                              # foldered {Domain}/V{n}. Distinct from each service's own
                              # {Service}.Contracts (below) — see "Two kinds of Contracts".
      ts/                    # generated TS API clients (from OpenAPI), one folder per service
  frontend/
    web/                     # Nx workspace: apps/shell + libs/{core,shared,feature-*}
tests/
  backend/
    Unit/                    # {Service}.Domain/Application unit tests, one folder per service
    Integration/             # PostgreSQL/Redis/Elasticsearch/RabbitMQ-backed tests (Testcontainers)
    Contract/                # API/event contract tests between services
    Architecture/            # NetArchTest fitness tests enforcing the dependency rules below
  agents/                    # pytest suites for the Python multi-agent system
  frontend/                  # Angular unit/e2e specs
docs/architecture/           # ADRs
infrastructure/
  docker/                    # extra Dockerfiles (per-service Dockerfiles live with the service instead)
  kubernetes/                # deferred — no manifests yet, created when a real deploy target exists
  terraform/                 # deferred — same
  helm/                      # deferred — same
  scripts/                   # setup/automation scripts
```

**Why `tests/` is top-level and not colocated**: a dedicated `tests/backend/Architecture`
project can assert the Clean Architecture dependency rules (below) as actual failing tests
(e.g. via NetArchTest — "no type in `*.Domain` may reference EF Core/MassTransit/Redis/HTTP")
rather than only documenting them in prose here. Grouping by test *type* also makes it obvious
which tests need which local infrastructure (`Integration` needs `docker compose up`;
`Unit`/`Architecture` don't).

**Two kinds of Contracts — do not confuse them**:
- **`{Service}.Contracts`** (per service, e.g. `Identity.Contracts`) — that service's own
  public REST request/response DTOs. Referenced by the gateway and by generated TS clients.
  Owned entirely by that service; other services never reference another service's
  `.Contracts` project.
- **`src/shared/contracts/dotnet` (`EduEco.Contracts`)** — cross-service *integration events*
  published to RabbitMQ. The one deliberate exception to "never share code between
  services," and even then it holds flat immutable records only, never a method, never a
  reference back into any service.

### Backend microservice layout (Clean Architecture, applies to every service under `src/backend/Services/*`)

Five projects per service, one-way dependencies `Api → Infrastructure → Application → Domain`,
`Contracts` referenced only by `Api` (and by the gateway/TS-client generation, never by
Domain/Application):

- **`{Service}.Domain`** — entities/aggregate roots (extend `BuildingBlocks.Domain.AggregateRoot<TId>`),
  value objects, domain events, `Result`/`Error` for expected failures. Zero references to
  EF Core, Dapper, StackExchange.Redis, Elasticsearch, MassTransit, or ASP.NET Core — ever.
  Enforced by `tests/backend/Architecture`, not just this document.
- **`{Service}.Application`** — MediatR commands/queries/handlers, FluentValidation
  validators, DTOs, and the *interfaces* Infrastructure implements. Defines ports; never
  references infrastructure packages.
- **`{Service}.Infrastructure`** — EF Core `DbContext` (PostgreSQL, command/write side),
  Dapper-based read repositories (query side), Elasticsearch adapters, MassTransit consumers +
  outbox wiring (via `BuildingBlocks.Messaging`), Redis decorators (via
  `BuildingBlocks.Caching`). The only project allowed to reference infra packages.
- **`{Service}.Contracts`** — that service's own public request/response DTOs (records only).
- **`{Service}.Api`** — `Program.cs` composition root, minimal-API endpoints (bind →
  `mediator.Send` → map response using `{Service}.Contracts` DTOs). No business logic here.

**Error handling convention**: `Result`/`Error` (from `BuildingBlocks.Domain`) for expected
domain-rule violations returned to callers. Exceptions are reserved for true infrastructure
failures (DB unreachable, etc.) — do not use exceptions as control flow for validation.

**Data access: EF Core + Dapper**: EF Core owns the write side — every command goes through
the `DbContext`/aggregate/`SaveChangesAsync`, so domain events and the outbox interceptor
fire correctly. Dapper owns the read side for queries that don't need change-tracking or
benefit from hand-tuned SQL (list/search/report-style queries) — a Dapper read-repository
implements the same `Application`-layer query-side interfaces (e.g. `IOrderReadRepository`)
using the same connection string as the service's `DbContext`, never a different database.
Never use Dapper to *write* — writes always go through EF Core so the outbox/domain-event
pipeline stays the single source of truth for state changes.

**Never share across services**: domain entities, EF `DbContext`/entity configs beyond the
base conventions in `BuildingBlocks.Infrastructure`, generic repository implementations, a
`{Service}.Contracts` project, or any compile-time `ProjectReference` between two services'
Application layers. Cross-service data needs go through an integration event
(`src/shared/contracts/dotnet`) or, rarely, an explicit gRPC call — never a direct reference.

### BuildingBlocks (7 focused projects, not one grab-bag)

Split so a service only pulls in what it actually needs (a service with no messaging
shouldn't reference MassTransit transitively):

- **`BuildingBlocks.Domain`** — `Entity<TId>`, `AggregateRoot<TId>`, `ValueObject`,
  `IDomainEvent`, `Result`/`Error`. Zero package references.
- **`BuildingBlocks.Application`** — MediatR pipeline behaviors (`ValidationBehavior`,
  `LoggingBehavior`, `UnitOfWorkBehavior` — commands don't call `SaveChanges` themselves),
  port interfaces (`ICacheService`, `IDistributedLock`, `ISearchIndex<T>`, `IUnitOfWork`,
  `ICurrentUser`), `PagedResult<T>`. References only `BuildingBlocks.Domain`.
- **`BuildingBlocks.Infrastructure`** — `SaveChangesInterceptor` that dispatches pending
  domain events via MediatR before the physical EF Core save; Dapper read-repository base
  helpers. References `BuildingBlocks.Application`. Has **no** reference to MassTransit —
  it knows nothing about the outbox.
- **`BuildingBlocks.Messaging`** — MassTransit + RabbitMQ bus configuration, the
  transactional-outbox wiring (`AddEntityFrameworkOutbox<TDbContext>` + `UseBusOutbox()` —
  not hand-rolled) **including the outbox model-builder entity registration**. References
  `BuildingBlocks.Application`. A service's own `DbContext.OnModelCreating` and
  `Program.cs` are what call into both Infrastructure's interceptor registration *and*
  Messaging's outbox wiring — Infrastructure and Messaging never reference each other.
- **`BuildingBlocks.Caching`** — `RedisCacheService : ICacheService`,
  `RedisDistributedLock : IDistributedLock` (hand-rolled `SET NX PX` + Lua release, not
  RedLock.net — single Redis instance per `docker-compose.yml` has no split-brain concern).
  References `BuildingBlocks.Application`.
- **`BuildingBlocks.Observability`** — structured logging (Serilog enrichers), correlation-id
  propagation, an OpenTelemetry tracing/metrics setup extension shared by every service +
  the gateway. Mostly infrastructure-side DI wiring, minimal Application-layer surface.
- **`BuildingBlocks.Security`** — `ICurrentUser`'s concrete implementation (reads claims from
  `HttpContext`), shared authorization policy names/constants, JWT-related helpers reused by
  both the gateway (early validation) and each service (defense-in-depth re-authorization).

### Redis key convention

`{service}:{entity}:{id}` for cache-aside, `{service}:lock:{resource}` for distributed
locks, `identity:blacklist:{jti}` for Identity's token revocation.

### Database migrations

Author per service: `dotnet ef migrations add <Name> --project src/backend/Services/<Service>/<Service>.Infrastructure --startup-project src/backend/Services/<Service>/<Service>.Api`.
Auto-migrate (`Database.Migrate()` on startup) only when `ASPNETCORE_ENVIRONMENT == Development`
— never wire auto-migrate into a Staging/Production path.

### Build / test / lint commands (scoped by what a phase touched)

Run only the block(s) matching the paths this phase changed — do not build/test unrelated
stacks. **Exception**: a change under `edueco/BuildingBlocks/**` or
`src/shared/contracts/dotnet/**` must build+test *every* service's `.sln`, since those are
referenced everywhere.

- **A .NET service** (`src/backend/Services/<Service>/**`, e.g. `Identity`):
  - Build: `dotnet build src/backend/Services/<Service>/<Service>.sln -c Release`
  - Test: `dotnet test src/backend/Services/<Service>/<Service>.sln -c Release` **and**
    `dotnet test tests/backend/Unit/<Service> -c Release` **and**
    `dotnet test tests/backend/Integration/<Service> -c Release` (if that project exists —
    integration tests need `docker compose up -d` first)
  - Lint: `dotnet format src/backend/Services/<Service>/<Service>.sln --verify-no-changes`

- **API Gateway** (`src/backend/ApiGateway/**`):
  - Build: `dotnet build src/backend/ApiGateway/ApiGateway.Api/ApiGateway.Api.csproj`
  - Test: `dotnet test tests/backend/Unit/ApiGateway`
  - Lint: `dotnet format src/backend/ApiGateway/ApiGateway.Api/ApiGateway.Api.csproj --verify-no-changes`

- **Shared BuildingBlocks** (`edueco/BuildingBlocks/**`) and **Contracts**
  (`src/shared/contracts/dotnet/**`): build+test the specific changed `.csproj` via
  `dotnet build|test EduEcosystem.sln -c Release` (the root solution covering all 7
  BuildingBlocks projects + `EduEco.Contracts` + the Architecture fitness-test project),
  **plus** the build+test commands for every service above (see Exception note).

- **Architecture fitness tests** (any backend change): `dotnet test tests/backend/Architecture
  -c Release` — must pass on every backend PR regardless of which service/BuildingBlocks
  project changed, since it's asserting repo-wide dependency rules.

- **Multi-agent Python system** (`src/agents/**`):
  - Test: `cd src/agents && uv run pytest`
  - Lint: `uv run ruff check .` and `uv run ruff format --check .`
  - Type-check: `uv run mypy .`

- **Angular frontend** (`src/frontend/web/**`):
  - Install (first run / lockfile changed): `cd src/frontend/web && npm ci`
  - Scoped to what changed: `npx nx affected -t lint,test,build --base=origin/dev`
  - If the phase touched one known project: `npx nx test <project>`, `npx nx lint <project>`, `npx nx build shell`

### Testing rules (code coverage)

**95% line coverage is required for every `{Service}.Domain` and `{Service}.Application`
project** (and `BuildingBlocks.Domain`/`BuildingBlocks.Application`) — these are the layers
that hold real business logic and are cheaply unit-testable in isolation, so there's no
excuse for gaps. A PR that drops either project's coverage below 95% fails the build and
must not be opened/merged.

**Exempt from the 95% gate**: `Infrastructure`, `Api`, `Contracts`, and the infra-flavored
BuildingBlocks projects (`Messaging`, `Caching`, `Observability`, `Security`). These are
thin wiring/composition-root/adapter code, verified by `tests/backend/Integration/<Service>`
(real Postgres/Redis/Elasticsearch/RabbitMQ via Testcontainers) rather than by line-coverage
percentage — chasing 95% line coverage on `Program.cs` or a `DbContext` migration produces
low-value tests, not confidence. Composition-root classes (`Program.cs`, `*ServiceCollectionExtensions.cs`)
must additionally be marked `[ExcludeFromCodeCoverage]` so they don't silently drag down a
project's number if it ever does get measured incidentally.

**Enforcement mechanism**: every `Unit` test project (`tests/backend/Unit/<Service>`,
`tests/backend/Unit/BuildingBlocks`) references `coverlet.msbuild` (not just
`coverlet.collector` — the MSBuild package is what makes `/p:Threshold` actually fail the
build, not just report a number). Run:

```
dotnet test <UnitTestProject> -c Release /p:CollectCoverage=true /p:Threshold=95 /p:ThresholdType=line /p:ThresholdStat=total
```

This is an **addition** to, not a replacement for, the existing per-stack test commands
above — run the normal `dotnet test` commands too; this coverage-gated invocation is the
one that specifically must pass on `{Service}.Domain`/`{Service}.Application` (and the
BuildingBlocks equivalents) before a PR opens.

### Local dev environment

`docker compose up -d` at repo root starts PostgreSQL, Elasticsearch, Redis, and RabbitMQ
(ports 5432/9200/6379/5672+15672). Dev-only credentials — see `docker-compose.yml` comments.
`make up`/`make down` wrap the same. The gateway runs containerized or via `dotnet run`;
Angular runs via `npx nx serve shell` (not containerized in dev, for fast HMR) pointed at the
gateway's local port.

### Package versions

`Directory.Packages.props` at repo root turns on Central Package Management
(`ManagePackageVersionsCentrally`) — every `.csproj`'s `<PackageReference>` omits `Version`;
the pinned version lives in exactly one place. When a phase adds the first reference to a
new package, add its `<PackageVersion>` entry to `Directory.Packages.props` in that same PR.

### Commit style

Conventional Commits (`feat:`, `fix:`, `chore:`, etc.).

### Branch naming

Automation uses `feature/{issue-number}-{slug}-phase-{n}` off `dev`.

## For the planning agent (`sdlc-01-plan.yml` / `sdlc-02-plan-command.yml`)

- Read this file, `README.md`, and everything under `docs/**/*.md` before drafting a plan.
- Break work into the smallest set of phases that each produce an independently reviewable
  PR (prefer 100-400 line diffs per phase over one large diff). For a new microservice,
  typical phasing is: (1) Domain + its unit tests, (2) Application + its unit tests, (3)
  Infrastructure + Contracts (+ integration tests), (4) Api — tests for a layer land in the
  same phase as that layer, not deferred to a final "tests" phase, since Domain/Application
  each carry their own 95% coverage gate (see "Testing rules") that must pass before that
  phase's PR merges. Combine into fewer phases if the service is small enough to stay within
  the diff-size guidance.
- Surface genuine ambiguity as explicit questions in the plan rather than guessing silently.

## For the implementation agent (`sdlc-03-develop-phase.yml`)

- Implement only the current phase's scope from the approved plan — do not scope-creep into
  later phases.
- Follow the conventions above, especially the Clean Architecture layer boundaries, the
  "never share across services" rule, and the EF Core (writes) / Dapper (reads) split —
  these are the most common places this kind of codebase silently degrades if not enforced
  per change.
- Run the build/test/lint commands above (scoped to what this phase touched), including the
  Architecture fitness tests for any backend change, before pushing; do not open a PR with
  failing checks.
- If the phase touched a `Domain` or `Application` project (service or BuildingBlocks), also
  run the coverage-gated command in "Testing rules (code coverage)" and do not open a PR
  below the 95% threshold — write more unit tests, don't lower the bar.

## Related docs

- `docs/SDLC_AUTOMATION.md` — full design of the autonomous issue→PR→merge pipeline.
- `docs/architecture/` — ADRs for the platform architecture decisions summarized above.
