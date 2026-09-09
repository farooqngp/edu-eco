# Build / test / lint commands, and code coverage

Referenced from `CLAUDE.md`. Run only the block(s) matching the paths a phase changed — do
not build/test unrelated stacks. **Exception**: a change under `edueco/BuildingBlocks/**` or
`src/shared/contracts/dotnet/**` must build+test *every* service's `.sln`, since those are
referenced everywhere.

## Commands, by stack

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

## Testing rules (code coverage)

**95% line coverage is required for every `{Service}.Domain` and `{Service}.Application`
project** (and `BuildingBlocks.Domain`/`BuildingBlocks.Application`) — these are the layers
that hold real business logic and are cheaply unit-testable in isolation, so there's no
excuse for gaps. A PR that drops either project's coverage below 95% fails the build and
must not be opened/merged.

**Exempt from the 95% gate**: `Infrastructure`, `Api`, `Contracts`, and the infra-flavored
BuildingBlocks projects (`Messaging`, `Caching`, `Observability`, `Security`). These are
thin wiring/composition-root/adapter code, verified by `tests/backend/Integration/<Service>`
(real Postgres/Redis/Elasticsearch/RabbitMQ via Testcontainers) rather than by line-coverage
percentage — chasing 95% line coverage on `Program.cs` or a migration runner produces
low-value tests, not confidence. Composition-root classes (`Program.cs`,
`*ServiceCollectionExtensions.cs`) must additionally be marked `[ExcludeFromCodeCoverage]`
so they don't silently drag down a project's number if it ever does get measured
incidentally.

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

**Table/column documentation enforcement** (see `data-access.md`): each service's
`tests/backend/Integration/<Service>` suite includes a test that runs that service's DbUp
migration scripts against a Testcontainers Postgres instance and asserts every table/column
has a `COMMENT` — this runs as part of that service's normal Integration test command
above, not a separate invocation.
