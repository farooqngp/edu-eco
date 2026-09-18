# edu-eco

Multi-tenant (school / district) education platform on .NET 10. OAuth 2.1 / OpenID Connect with OpenIddict, RBAC plus
permissions, Dapper for domain data, DbUp for every schema change.

```
SPA ──cookie──> EduEco.Bff (YARP + OIDC, PAR, private_key_jwt) ──Bearer──┐
Mobile ──code + PKCE + DPoP──> EduEco.Identity (OpenIddict) ──────────────┼─> EduEco.Api (JWT, DPoP, permissions)
Service ──client_credentials (private_key_jwt)──> EduEco.Identity ────────┘
EduEco.Identity ──back-channel logout (logout+jwt)──> EduEco.Bff
SQL Server (auth / dbo / bff schemas, DbUp) · Redis (L2 cache, invalidation bus, replay cache) · Mailpit (dev SMTP)
```

| Project | README |
|---|---|
| Authorization server | [EduEco/EduEco.Identity/README.md](EduEco/EduEco.Identity/README.md) |
| Resource server | [EduEco/EduEco.Api/README.md](EduEco/EduEco.Api/README.md) |
| Backend for Frontend | [EduEco/EduEco.Bff/README.md](EduEco/EduEco.Bff/README.md) |
| Database migrator and production runbook | [EduEco/EduEco.Database/README.md](EduEco/EduEco.Database/README.md) |

## Prerequisites

- .NET 10 SDK (see `EduEco/global.json`) and Docker Desktop.
- IDE: **Visual Studio 2026** (18.x), VS Code with C# Dev Kit, or Rider. Visual Studio 2022 cannot load the solution:
  its MSBuild 17.14 cannot use the .NET 10.0.4xx SDK, and .NET 10 targeting is not supported in VS 2022
  (`NETSDK1233`, an error here because warnings are treated as errors).

## Local stack (Docker)

1. Copy `.env.example` to `.env` and set every value.
2. Generate development certificates (token signing/encryption, HTTPS, client keys for `private_key_jwt`):

   ```powershell
   powershell -ExecutionPolicy Bypass -File tools/New-DevCertificates.ps1
   ```

3. Start everything:

   ```bash
   docker compose up -d --build
   ```

| URL | Service |
|---|---|
| `https://localhost:7100` | SPA via BFF |
| `https://localhost:7013` | Identity (discovery: `/.well-known/openid-configuration`) |
| `https://localhost:7037/scalar` | API reference |
| `http://localhost:8025` | Mailpit (password reset / confirmation emails) |

## Tests

```bash
cd EduEco
dotnet test --project Test/EduEco.Identity.IntegrationTests
```

Five suites (Database, Infrastructure, Identity, Api, Bff). Integration suites start SQL Server and Redis with
Testcontainers, so Docker must be running.

## CI/CD (GitHub Actions)

| Workflow | Trigger | Does |
|---|---|---|
| `ci.yml` | push / PR to `main`, `dev` | Build (warnings as errors, NuGet audit), vulnerable-package gate, 5 test suites, migration script artifact + idempotency check, 4 images to GHCR with SBOM and signed provenance |
| `codeql.yml` | push / PR / weekly | CodeQL `security-extended` for C# and workflow files |
| `db-migrate.yml` | manual | Plan (dry run + `--script-out`, attestation verified) then apply after environment approval |
| `zap-baseline.yml` | weekly / manual / PRs touching hosts | OWASP ZAP baseline against the full compose stack; accepted alerts in `.zap/rules.tsv` |

Actions are pinned to commit SHAs; Dependabot (`.github/dependabot.yml`) updates NuGet, Actions and base images.
Restore uses `EduEco/nuget.config` (nuget.org only, package source mapping).
