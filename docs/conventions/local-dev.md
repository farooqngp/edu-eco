# Local dev, Redis keys, package versions, commit/branch conventions

Referenced from `CLAUDE.md`.

## Local dev environment

`docker compose up -d` at repo root starts PostgreSQL, Elasticsearch, Redis, and RabbitMQ
(ports 5432/9200/6379/5672+15672). Dev-only credentials — see `docker-compose.yml`
comments. `make up`/`make down` wrap the same. The gateway runs containerized or via
`dotnet run`; Angular runs via `npx nx serve shell` (not containerized in dev, for fast
HMR) pointed at the gateway's local port.

## Redis key convention

`{service}:{entity}:{id}` for cache-aside, `{service}:lock:{resource}` for distributed
locks, `identity:blacklist:{jti}` for Identity's token revocation.

## Package versions

`Directory.Packages.props` at repo root turns on Central Package Management
(`ManagePackageVersionsCentrally`) — every `.csproj`'s `<PackageReference>` omits `Version`;
the pinned version lives in exactly one place. When a phase adds the first reference to a
new package, add its `<PackageVersion>` entry to `Directory.Packages.props` in that same PR.

## Commit style

Conventional Commits (`feat:`, `fix:`, `chore:`, etc.).

## Branch naming

Automation uses `feature/{issue-number}-{slug}-phase-{n}` off `dev`.
