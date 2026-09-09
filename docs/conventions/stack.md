# Stack

Referenced from `CLAUDE.md`. Full rationale: `docs/architecture/PLATFORM_ARCHITECTURE.md`.

- **.NET 10** (C#) — microservices under `src/backend/Services/*`, the API gateway under
  `src/backend/ApiGateway/`, and shared libraries under `edueco/BuildingBlocks/*`. Each
  microservice follows Clean Architecture (see `docs/conventions/backend-services.md`).
- **Python 3.13** (`uv`, FastAPI) — the multi-agent AI system under `src/agents/`
  (planner/researcher/executor/reviewer roles + shared tools/workflow orchestration).
- **Angular** (latest, Nx-managed workspace) — frontend under `src/frontend/web/`.
- **PostgreSQL** — transactional data, one logical database per microservice (never shared
  across services). **All** data access — reads and writes — goes through Dapper +
  Dapper Contrib. No EF Core anywhere in this repo (see `docs/conventions/data-access.md`).
- **Elasticsearch** — non-transactional/search/read-model data only. Never the source of
  truth; always populated asynchronously from the transactional side via the outbox
  pattern (see `docs/conventions/data-access.md`). Safe to fully rebuild from PostgreSQL at
  any time.
- **Redis** — cache-aside, distributed locks, Identity's token/session blacklist.
- **RabbitMQ + MassTransit** — inter-service integration events, using a hand-rolled
  transactional outbox (MassTransit's built-in outbox only supports EF Core/MongoDB, not
  Dapper — see `docs/conventions/data-access.md`).
- **YARP** — the single API gateway. The Angular app and any external client call only the
  gateway; microservices are never called directly.
