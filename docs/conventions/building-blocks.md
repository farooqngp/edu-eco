# BuildingBlocks (7 focused projects, not one grab-bag)

Referenced from `CLAUDE.md`. Split so a service only pulls in what it actually needs (a
service with no messaging shouldn't reference MassTransit transitively). Live under
`edueco/BuildingBlocks/`.

- **`BuildingBlocks.Domain`** — `Entity<TId>` (implements `IAuditableEntity` and
  `ISoftDeletableEntity` directly — see `data-access.md`), `AggregateRoot<TId>`,
  `ValueObject`, `IDomainEvent`, `Result`/`Error`. Zero package references.
- **`BuildingBlocks.Application`** — MediatR pipeline behaviors (`ValidationBehavior`,
  `LoggingBehavior`, `UnitOfWorkBehavior` — commands don't call `SaveChanges` themselves),
  port interfaces (`ICacheService`, `IDistributedLock`, `ISearchIndex<T>`, `IUnitOfWork`,
  `ICurrentUser`), `PagedResult<T>`. References only `BuildingBlocks.Domain`.
- **`BuildingBlocks.Infrastructure`** — Dapper/Dapper Contrib base repository conventions
  (`AuditableRepository<TEntity,TId>`), the DbUp migration runner, the soft-delete
  `v_{table}` view convention. References `BuildingBlocks.Application`. Has **no** reference
  to MassTransit or EF Core — it has no outbox knowledge at all, and no ORM
  change-tracking. See `data-access.md` for the full detail.
- **`BuildingBlocks.Messaging`** — MassTransit + RabbitMQ bus configuration, plus the
  hand-rolled transactional outbox (`outbox_messages` table + `OutboxRelayService`
  background poller — see `data-access.md`). References `BuildingBlocks.Application`. A
  service's own composition root (`Program.cs`) is what calls into both Infrastructure's
  repository conventions *and* Messaging's outbox wiring — Infrastructure and Messaging
  never reference each other.
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

All 7 projects are currently empty `.csproj` scaffolds — package/project references are
wired ahead of time, but zero `.cs` files exist yet.
