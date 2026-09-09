# Data access: Dapper + Dapper Contrib (no EF Core, anywhere)

Referenced from `CLAUDE.md`. Full rationale: `docs/architecture/PLATFORM_ARCHITECTURE.md`'s
"Data access" section. This is the convention every `{Service}.Infrastructure` project and
`BuildingBlocks.Infrastructure`/`BuildingBlocks.Messaging` implement.

**No EF Core reference exists anywhere in this repo — read or write side.**

## Reads vs. writes

- **Dapper Contrib** (`Insert`/`Update`/`Get`/`GetAll` extension methods) for simple
  single-table writes — wrapped by the shared `AuditableRepository<TEntity,TId>` base in
  `BuildingBlocks.Infrastructure` (see "Audit columns + soft delete" below). Contrib only
  ever operates on one table at a time.
- **Hand-written SQL via plain Dapper** for anything multi-table, batch, or read-side
  (list/search/report-style queries). A Dapper read-repository implements the same
  Application-layer query port (e.g. `IOrderReadRepository`) using the *same* connection
  string as the service's writes — never a separate database.
- Read-repositories query the soft-delete view (`v_{table}`, see below) by default, never
  the base table, unless the method is explicitly named `...IncludingDeletedAsync`.

## Unit of work without change-tracking

`IUnitOfWork` (`BuildingBlocks.Application`) wraps a single `IDbConnection`/`IDbTransaction`
pair per request. Repositories take the ambient transaction rather than owning their own
connection, so every write in one command handler commits atomically.
`UnitOfWorkBehavior` (MediatR pipeline) opens the transaction before the handler runs and
commits it after, only if the handler's `Result` is successful.

## Domain event dispatch without an interceptor

Repositories register the aggregates they touch on the ambient `IUnitOfWork`
(`unitOfWork.TrackAggregate(entity)` on Insert/Update). After `UnitOfWorkBehavior` commits
the transaction, it walks the tracked aggregates and dispatches their `DomainEvents` via
MediatR — explicit rather than the implicit dispatch an EF `SavingChangesAsync` interceptor
would have done.

## Migrations: DbUp

Plain versioned `.sql` scripts, checksummed and applied in order — no code-gen, no EF
migrations. Scripts live under `{Service}.Infrastructure/Migrations/NNNN_description.sql`,
applied via a small `MigrationRunner` in `BuildingBlocks.Infrastructure` wrapping
`DeployChanges.To.PostgresqlDatabase(...).WithScriptsEmbeddedInAssembly(...)`
(`dbup-postgresql` package). Auto-run only when `ASPNETCORE_ENVIRONMENT == Development` —
never wire auto-migrate into a Staging/Production path.

Author per service: add a new numbered `.sql` file under that service's
`Infrastructure/Migrations/` folder; do not edit a script that has already shipped/run
anywhere — DbUp checksums applied scripts and will fail on a modified one.

## Table/column documentation

**Every `CREATE TABLE` migration script must include `COMMENT ON TABLE ...` and
`COMMENT ON COLUMN ...` statements for the table and every column, in the same script.**
This becomes real PostgreSQL catalog metadata — queryable via `information_schema` and
`pg_catalog.col_description()` — so it can never drift from the real schema the way a
separate hand-maintained data dictionary would, and it's directly usable as a RAG source
later without a separate documentation step.

Comment content should say: what the table/column is *for* (not just its name/type
restated), and any non-obvious constraint or relationship. Example:

```sql
CREATE TABLE users (
    id              uuid PRIMARY KEY,
    email           citext NOT NULL UNIQUE,
    display_name    text NOT NULL,
    created_at_utc  timestamptz NOT NULL,
    created_by      text NULL,
    last_modified_at_utc timestamptz NULL,
    last_modified_by     text NULL,
    is_deleted      boolean NOT NULL DEFAULT false,
    deleted_at_utc  timestamptz NULL,
    deleted_by      text NULL
);

COMMENT ON TABLE users IS 'A registered platform user/account. One row per identity, regardless of how many roles they hold.';
COMMENT ON COLUMN users.email IS 'Login identifier and notification address; case-insensitive (citext) and unique platform-wide.';
COMMENT ON COLUMN users.display_name IS 'Human-readable name shown in the UI; not used for authentication or lookup.';
```

**Enforcement**: an integration test (Testcontainers Postgres) runs a service's migration
scripts against a throwaway database, then asserts every table and every column has a
non-null comment — fails the build if any table ships undocumented. This test lives
alongside that service's other `tests/backend/Integration/<Service>` tests.

## Audit columns + soft delete

`IAuditableEntity` and `ISoftDeletableEntity` are implemented **directly by
`BuildingBlocks.Domain.Entity<TId>`** — not an opt-in interface a concrete entity might
forget to add. Every table-backed entity gets both automatically:

```csharp
public interface IAuditableEntity
{
    DateTime CreatedAtUtc { get; set; }
    string? CreatedBy { get; set; }
    DateTime? LastModifiedAtUtc { get; set; }
    string? LastModifiedBy { get; set; }
}

public interface ISoftDeletableEntity
{
    bool IsDeleted { get; set; }
    DateTime? DeletedAtUtc { get; set; }
    string? DeletedBy { get; set; }
}
```

Every table needs the matching `created_at_utc`, `created_by`, `last_modified_at_utc`,
`last_modified_by`, `is_deleted`, `deleted_at_utc`, `deleted_by` columns — add them in the
same `CREATE TABLE` migration script as the rest of the table (with comments, per above).

`AuditableRepository<TEntity,TId>` (`BuildingBlocks.Infrastructure`, wrapping Dapper
Contrib) is what every service repository inherits from for its simple CRUD. It:

1. Stamps `CreatedAtUtc`/`CreatedBy` (from `ICurrentUser`) before every `Insert`.
2. Stamps `LastModifiedAtUtc`/`LastModifiedBy` before every `Update`.
3. Turns "delete" into `UPDATE ... SET is_deleted = true, deleted_at_utc = ..., deleted_by =
   ...` — **no physical `DELETE` is ever issued** by a repository built on this base.
4. Writes the audit-log row (see below) inside the *same* `IDbTransaction`.

**Soft-delete filtering is convention plus a generated view, not automatic.** Raw Dapper has
no equivalent of EF's global `HasQueryFilter` — there is no compiler-enforced way to stop a
hand-written query from forgetting `WHERE is_deleted = false`. The convention that replaces
it: every DbUp migration that creates a soft-deletable table also creates a matching view:

```sql
CREATE VIEW v_users AS SELECT * FROM users WHERE is_deleted = false;
```

Read-repositories query `v_{table}` by default. Only an explicitly named method (e.g.
`GetIncludingDeletedAsync`) queries the base table directly — this makes "I need to see
deleted rows" a deliberate, greppable exception rather than a silent default.

## Audit-trail logging

`AuditableRepository<TEntity,TId>` writes one `AuditLogEntry` row per Insert/Update/soft-delete,
inside the same transaction as the business write (atomicity is free — the audit trail can
never drift out of sync with what actually happened):

```csharp
public sealed class AuditLogEntry
{
    public Guid Id { get; init; }
    public string EntityName { get; init; } = default!;
    public string EntityId { get; init; } = default!;   // stringified — can't be generic over every TId
    public AuditAction Action { get; init; }             // Created | Updated | SoftDeleted
    public DateTime ChangedAtUtc { get; init; }
    public string? ChangedBy { get; init; }
    public string? OldValues { get; init; }              // JSON text
    public string? NewValues { get; init; }              // JSON text
    public string? CorrelationId { get; init; }          // from Activity.Current?.Id
}
```

`OldValues`/`NewValues` are plain `string` in the shared kernel (not native `jsonb`) —
mapping to `jsonb` happens in each service's own migration script, keeping
`BuildingBlocks.Infrastructure` free of any Postgres-specific type dependency.

## Outbox pattern (no MassTransit built-in support for Dapper)

MassTransit's transactional outbox only supports EF Core and MongoDB —
[confirmed](https://github.com/MassTransit/MassTransit/discussions/5600), no plans to add
Dapper support. Replacement, hand-rolled in `BuildingBlocks.Messaging`:

- An `outbox_messages` table, written via the *same* `IDbTransaction` as the business write
  — atomicity is free, no two-phase commit needed.
- An `OutboxRelayService : BackgroundService` that polls unpublished rows, calls
  `IBus.Publish`, and marks them dispatched.

This is a standard transactional-outbox-plus-relay implementation, just built by hand
instead of configured via `AddEntityFrameworkOutbox<TDbContext>`.
