# Directory layout

Referenced from `CLAUDE.md`.

```
edueco/
  BuildingBlocks/            # 7 focused shared libraries — see building-blocks.md
    BuildingBlocks.Domain/
    BuildingBlocks.Application/
    BuildingBlocks.Infrastructure/     # Dapper/Dapper Contrib base conventions, DbUp migrations
    BuildingBlocks.Observability/      # logging/tracing/metrics conventions
    BuildingBlocks.Messaging/          # MassTransit/RabbitMQ + hand-rolled outbox
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
    workflows/                # multi-agent orchestration graphs
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
docs/
  architecture/              # ADRs + PLATFORM_ARCHITECTURE.md
  conventions/                # this directory — CLAUDE.md's breakdown files, by topic
infrastructure/
  docker/                    # extra Dockerfiles (per-service Dockerfiles live with the service instead)
  kubernetes/                # deferred — no manifests yet, created when a real deploy target exists
  terraform/                 # deferred — same
  helm/                      # deferred — same
  scripts/                   # setup/automation scripts
```

**Why `tests/` is top-level and not colocated**: a dedicated `tests/backend/Architecture`
project can assert the Clean Architecture dependency rules (see `backend-services.md`) as
actual failing tests (e.g. via NetArchTest — "no type in `*.Domain` may reference
Dapper/Redis/MassTransit/HTTP") rather than only documenting them in prose. Grouping by test
*type* also makes it obvious which tests need which local infrastructure (`Integration`
needs `docker compose up`; `Unit`/`Architecture` don't).

## Two kinds of Contracts — do not confuse them

- **`{Service}.Contracts`** (per service, e.g. `Identity.Contracts`) — that service's own
  public REST request/response DTOs. Referenced by the gateway and by generated TS clients.
  Owned entirely by that service; other services never reference another service's
  `.Contracts` project.
- **`src/shared/contracts/dotnet` (`EduEco.Contracts`)** — cross-service *integration events*
  published to RabbitMQ. The one deliberate exception to "never share code between
  services," and even then it holds flat immutable records only, never a method, never a
  reference back into any service.
