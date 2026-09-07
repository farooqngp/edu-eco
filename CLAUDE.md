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

- **.NET 10** (C#) — microservices under `src/services/*`, the API gateway under
  `src/gateway/`, and shared libraries under `src/shared/`. Each microservice follows
  Clean Architecture (see "Backend microservice layout" below).
- **Python 3.13** (`uv`, FastAPI) — the Agentic (AI/LLM-agent) service under
  `src/agentic/`, hexagonal/ports-and-adapters layout.
- **Angular** (latest, Nx-managed workspace) — frontend under `src/frontend/web/`.
- **SQL Server** — transactional data, one logical database per microservice (never
  shared across services).
- **Elasticsearch** — non-transactional/search/read-model data only. Never the source of
  truth; always populated asynchronously from the transactional side via the outbox
  pattern (see below). Safe to fully rebuild from SQL Server at any time.
- **Redis** — cache-aside, distributed locks, Identity's token/session blacklist.
- **RabbitMQ + MassTransit** — inter-service integration events, using MassTransit's
  EF Core transactional outbox (not a hand-rolled poller).
- **YARP** — the single API gateway. The Angular app and any external client call only
  the gateway; microservices are never called directly.

### Directory layout

```
src/
  gateway/                # YARP reverse proxy — Gateway.Api, Gateway.Api.Tests
  services/
    Identity/             # Identity.{Domain,Application,Infrastructure,Api} + 3 test projects
    Catalog/              # placeholder core-domain service — same pattern, rename when the real domain lands
    Notifications/        # same pattern
  agentic/                # Python FastAPI agentic service (hexagonal layout)
  shared/
    BuildingBlocks/       # BuildingBlocks.{Domain,Application,Infrastructure} — see below
    contracts/
      dotnet/             # EduEco.Contracts.csproj — integration event DTOs, foldered {Domain}/V{n}
      ts/                 # generated TS API clients (from OpenAPI), one folder per service
  frontend/
    web/                  # Nx workspace: apps/shell + libs/{core,shared,feature-*}
docs/architecture/        # ADRs
```

### Backend microservice layout (Clean Architecture, applies to every service under `src/services/*`)

Four projects, one-way dependencies `Api → Infrastructure → Application → Domain`:

- **`{Service}.Domain`** — entities/aggregate roots (extend `BuildingBlocks.Domain.AggregateRoot<TId>`),
  value objects, domain events, `Result`/`Error` for expected failures. Zero references to
  EF Core, StackExchange.Redis, Elasticsearch, or MassTransit — ever.
- **`{Service}.Application`** — MediatR commands/queries/handlers, FluentValidation
  validators, DTOs, and the *interfaces* Infrastructure implements. Defines ports; never
  references infrastructure packages.
- **`{Service}.Infrastructure`** — EF Core `DbContext` (SQL Server), repositories,
  Elasticsearch adapters, Redis decorators, MassTransit consumers + outbox wiring. The
  only project allowed to reference infra packages.
- **`{Service}.Api`** — `Program.cs` composition root, minimal-API endpoints (bind →
  `mediator.Send` → map response). No business logic here.

**Error handling convention**: `Result`/`Error` (from `BuildingBlocks.Domain`) for expected
domain-rule violations returned to callers. Exceptions are reserved for true infrastructure
failures (DB unreachable, etc.) — do not use exceptions as control flow for validation.

**Never share across services**: domain entities, EF `DbContext`/entity configs beyond the
base conventions in `BuildingBlocks.Infrastructure`, generic repository implementations, or
any compile-time `ProjectReference` between two services' Application layers. Cross-service
data needs go through an integration event (`src/shared/contracts/dotnet`) or, rarely, an
explicit gRPC call — never a direct reference.

### Redis key convention

`{service}:{entity}:{id}` for cache-aside, `{service}:lock:{resource}` for distributed
locks, `identity:blacklist:{jti}` for Identity's token revocation.

### Database migrations

Author per service: `dotnet ef migrations add <Name> --project src/services/<Service>/<Service>.Infrastructure --startup-project src/services/<Service>/<Service>.Api`.
Auto-migrate (`Database.Migrate()` on startup) only when `ASPNETCORE_ENVIRONMENT == Development`
— never wire auto-migrate into a Staging/Production path.

### Build / test / lint commands (scoped by what a phase touched)

Run only the block(s) matching the paths this phase changed — do not build/test unrelated
stacks. **Exception**: a change under `src/shared/**` must build+test *every* service's
`.sln`, since BuildingBlocks/Contracts are referenced everywhere.

- **A .NET service** (`src/services/<Service>/**`, e.g. `Identity`):
  - Build: `dotnet build src/services/<Service>/<Service>.sln -c Release`
  - Test: `dotnet test src/services/<Service>/<Service>.sln -c Release`
  - Lint: `dotnet format src/services/<Service>/<Service>.sln --verify-no-changes`

- **Gateway** (`src/gateway/**`):
  - Build: `dotnet build src/gateway/Gateway.Api/Gateway.Api.csproj`
  - Test: `dotnet test src/gateway/Gateway.Api.Tests/Gateway.Api.Tests.csproj`
  - Lint: `dotnet format src/gateway/Gateway.Api/Gateway.Api.csproj --verify-no-changes`

- **Shared BuildingBlocks/Contracts** (`src/shared/**`): same three commands as above,
  scoped to the changed `.csproj`, **plus** the build+test commands for every service above
  (see exception note).

- **Agentic service** (`src/agentic/**`):
  - Test: `cd src/agentic && uv run pytest`
  - Lint: `uv run ruff check .` and `uv run ruff format --check .`
  - Type-check: `uv run mypy agentic_service`

- **Angular frontend** (`src/frontend/web/**`):
  - Install (first run / lockfile changed): `cd src/frontend/web && npm ci`
  - Scoped to what changed: `npx nx affected -t lint,test,build --base=origin/dev`
  - If the phase touched one known project: `npx nx test <project>`, `npx nx lint <project>`, `npx nx build shell`

### Local dev environment

`docker compose up -d` at repo root starts SQL Server, Elasticsearch, Redis, and RabbitMQ
(ports 1433/9200/6379/5672+15672). Dev-only credentials — see `docker-compose.yml` comments.
The gateway runs containerized or via `dotnet run`; Angular runs via `npx nx serve shell`
(not containerized in dev, for fast HMR) pointed at the gateway's local port.

### Commit style

Conventional Commits (`feat:`, `fix:`, `chore:`, etc.).

### Branch naming

Automation uses `feature/{issue-number}-{slug}-phase-{n}` off `dev`.

## For the planning agent (`sdlc-01-plan.yml` / `sdlc-02-plan-command.yml`)

- Read this file, `README.md`, and everything under `docs/**/*.md` before drafting a plan.
- Break work into the smallest set of phases that each produce an independently reviewable
  PR (prefer 100-400 line diffs per phase over one large diff). For a new microservice,
  typical phasing is: (1) Domain, (2) Application, (3) Infrastructure, (4) Api + tests —
  or combine into fewer phases if the service is small enough to stay within the diff-size
  guidance.
- Surface genuine ambiguity as explicit questions in the plan rather than guessing silently.

## For the implementation agent (`sdlc-03-develop-phase.yml`)

- Implement only the current phase's scope from the approved plan — do not scope-creep into
  later phases.
- Follow the conventions above, especially the Clean Architecture layer boundaries and the
  "never share across services" rule — these are the most common places this kind of
  codebase silently degrades if not enforced per change.
- Run the build/test/lint commands above (scoped to what this phase touched) before
  pushing; do not open a PR with failing checks.

## Related docs

- `docs/SDLC_AUTOMATION.md` — full design of the autonomous issue→PR→merge pipeline.
- `docs/architecture/` — ADRs for the platform architecture decisions summarized above.
