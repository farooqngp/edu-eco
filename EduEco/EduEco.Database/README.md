# EduEco.Database — DbUp migrator

Only component allowed to run DDL. Apps (Api, Identity) never migrate at startup.

## Pipeline

| Order | Folder | DbUp type | Journaled | Rules |
|---|---|---|---|---|
| 0 | `Scripts/00_PreDeploy` | RunAlways | no | Checks only; idempotent |
| 1 | `Scripts/01_Migrations` | RunOnce | yes (`migration.SchemaVersions`) | `V####__desc.sql`, immutable, forward-only, registered in `manifest.json` |
| 2 | `Scripts/02_ReferenceData` | RunAlways | no | Idempotent `MERGE`/upsert; mirrors `EduEco.Core.Authorization` (tests verify) |
| 3 | `Scripts/03_PostDeploy` | RunAlways | no | Grants, stats; idempotent |
| 99 | `Scripts/99_Dev` | RunOnce | yes | `DOTNET_ENVIRONMENT=Development` only |

- One transaction per script (`SET XACT_ABORT ON` in every script).
- Journal names are `{folder}/{file}` → stable across OS / namespace renames.
- `manifest.json` pins SHA-256 (LF-normalised) of every migration. Migrator + unit tests fail on edited, deleted or unregistered migrations.

## Key strategy (high-volume)

- **No `uniqueidentifier` columns** (enforced by unit + integration tests).
- Surrogate PK = `[Id] bigint NOT NULL IDENTITY(1, 1)`, `PRIMARY KEY CLUSTERED` → narrow (8 bytes), ever-increasing, append-only inserts, no page splits; every FK/nonclustered index carries 8 bytes instead of 16.
- Code-owned catalogues (`auth.Permissions`) use explicit `bigint` ids (no IDENTITY); Dapper entity `[ExplicitKey]`.
- `auth.AspNetRoles` is `IDENTITY(1000, 1)`: ids 1–999 reserved for system roles inserted by `R0001` with `IDENTITY_INSERT`.
- Natural/business keys (`Tenants.Code`, `Permissions.Name`) are `UNIQUE NONCLUSTERED`.
- Dapper entities: `[Key] long Id` → Dapper.Contrib populates the IDENTITY on insert; never set `Id` before insert.
- Identity claim tables keep `int IDENTITY` (fixed by ASP.NET Core Identity; low volume).
- Sequential ids are enumerable: never rely on id secrecy — every access goes through tenant guards + resource-based authorization (P3).
- Very hot tenant-owned tables (e.g. grades, attendance): consider `PRIMARY KEY NONCLUSTERED (Id)` + `CLUSTERED (TenantId, Id)` for tenant locality, or partitioning — decide per table with measured workload.

## Commands

```bash
# Local (DOTNET_ENVIRONMENT=Development, connection string via user-secrets or env)
dotnet run --project EduEco.Database -- migrate --ensure-db --seed-clients --seed-dev-users
dotnet run --project EduEco.Database -- --dry-run
dotnet run --project EduEco.Database -- --script-out pending.sql
dotnet run --project EduEco.Database -- print-manifest
dotnet run --project EduEco.Database -- generate-auth-ddl --output auth.sql
```

Exit codes: `0` success, `1` failure (incl. manifest violation), `2` bad arguments, `130` cancelled.

## Adding a migration

1. Add `Scripts/01_Migrations/V00NN__short_description.sql` (next number; contiguity is tested).
2. Guard with `IF NOT EXISTS` / `IF OBJECT_ID(...) IS NULL` where practical.
3. `dotnet run --project EduEco.Database -- print-manifest` → copy **only the new entry** into `Scripts/manifest.json`.
4. Breaking change? Expand/contract across releases: add → backfill → switch code → drop in a later release.
5. Never edit or delete a released migration — write a new one.

### Identity / OpenIddict package upgrades

`AuthDbContext` (EF Core) has no migrations. After upgrading `Microsoft.AspNetCore.Identity.EntityFrameworkCore` or `OpenIddict.EntityFrameworkCore`:
run `generate-auth-ddl`, diff against `V0002`/`V0003`, author a new V-script for the delta. `AuthStoreTests` (integration) catch drift.

## Production runbook

Automated by GitHub Actions (`.github/workflows`):

1. **CI** (`ci.yml`): unit tests (manifest, reference-data mirroring) and integration tests (Testcontainers). Job
   *Migration script review* generates the full script from an empty database (artifact `migration-script`), applies it and
   proves a re-run has no pending journaled scripts. Pushes publish `ghcr.io/<owner>/edueco-db-migrator:sha-<commit>` with a
   signed build provenance attestation.
2. **Plan** (`db-migrate.yml`, manual, environment `<env>-plan`): verifies the image attestation, runs `--dry-run` and
   `--script-out pending.sql` against the target database, uploads `migration-plan-<env>` for DBA review.
3. **Approval gate**: the `apply` job waits on the protected environment `<env>` (required reviewers).
4. **Backup / PITR checkpoint** of the target database (checklist in the job summary).
5. **Apply**: the same image runs `migrate` with the **DDL login** (`db_ddladmin` + `db_datareader/writer`). Never pass
   `--ensure-db` or `--seed-dev-users`. `--seed-clients` registers public keys only (`PublicKeyCertificatePath`); the
   seeder refuses client secrets in Production.
6. **Deploy apps** only after exit code `0`. App logins are members of role `eduEco_app` (DML only, created by `03_PostDeploy`).
7. **Rollback**: forward-fix with a new migration; restore from checkpoint only for data-destructive failures.

## Local Docker

From repo root (copy `.env.example` → `.env` first):

```bash
docker compose up -d sqlserver
docker compose run --rm --build db-migrator
```

SQL Server is exposed on `localhost,14333` (override with `SQL_PORT`).

Client seeds (`appsettings.Development.json`) use `private_key_jwt` for every confidential client. Relative
`PublicKeyCertificatePath` values resolve against the project folder; generate the keys first with
`tools/New-DevCertificates.ps1` (writes `certs/*-client.pfx` and the public `*.cer`). For manual token requests as
`eduEco-svc-dev`, create a one-time assertion with `tools/New-ClientAssertion.ps1`.
