# EduEco — Identity, Authentication and Authorization: Implementation Context

Companion to [EduEco-Identity-Authentication-Authorization-Implementation-Plan.md](EduEco-Identity-Authentication-Authorization-Implementation-Plan.md).
The plan describes **what** was built. This document records **why**: the original request, every decision and its
source, the constraints that shaped the work, problems met along the way, and the state needed to resume.

| | |
|---|---|
| **Repository** | `github.com/farooqngp/edu-eco`, working branch `dev` (main branch `main`) |
| **Solution** | `EduEco/EduEco.slnx` (.NET 10) |
| **State** | Phases 1–5 implemented and verified locally; nothing committed yet |
| **Last updated** | 2026-09-18 |

---

## 1. Original request

> Act as a principal architect. Implement authentication and authorization for the API solution using OAuth identity.
> Review the solution and give a plan based on current industry standards.

Starting point: a blank template. `EduEco.Api` and `EduEco.Identity` held WeatherForecast scaffolds, `Core`,
`Application`, `Infrastructure` and `ServiceRegistry` were empty, `EduEco.Database` was a hello-world console, and there
were no tests. `EduEco.Api` called `UseAuthorization()` without `UseAuthentication()`.

## 2. Decision log

Decisions are listed in the order they were made, with their source.

| # | Topic | Decision | Source |
|---|---|---|---|
| 1 | Authorization server | OpenIddict (recommended option) | Stakeholder answer |
| 2 | Client types | SPA, mobile app, service-to-service | Stakeholder answer |
| 3 | Tenancy | Multi-tenant (schools / districts) | Stakeholder answer |
| 4 | Authorization model | RBAC + permissions | Stakeholder answer |
| 5 | Database provider | SQL Server | Stakeholder answer (open item) |
| 6 | Hosting | Local Docker for now | Stakeholder answer (open item) |
| 7 | External federation | Not considered now | Stakeholder answer (open item) |
| 8 | ORM | Dapper + Dapper.Contrib; generic repository pattern for commands, Dapper for queries | Stakeholder instruction |
| 9 | Database migrations | DbUp (or equivalent) with a production migration plan; reuse the existing Database folder | Stakeholder instruction |
| 10 | Identity/OpenIddict persistence | Hybrid: EF Core only inside the Identity/OpenIddict stores, no EF migrations, DbUp owns the schema | Stakeholder answer |
| 11 | Command repository base | Dapper.Contrib (as requested) | Stakeholder answer |
| 12 | Primary keys | All keys high-volume compatible: `bigint IDENTITY`; remove GUID keys | Stakeholder instruction |
| 13 | SPA architecture | Backend-for-Frontend (tokens never in the browser) | Architect decision, accepted with plan |
| 14 | Permissions in tokens | Not in tokens; resolved server-side per (user, tenant) and cached | Architect decision |
| 15 | Tenant per token | One tenant per access token; switching tenant means a new token | Architect decision |
| 16 | Phase 5 CI/CD platform | GitHub Actions | Stakeholder answer |
| 17 | NuGet source | Repository `nuget.config` with nuget.org only | Stakeholder answer |
| 18 | Email | SMTP, Mailpit for development | Stakeholder answer |
| 19 | Business endpoints | Do not implement now | Stakeholder instruction |
| 20 | Login / register / forgot password | Hosted Identity pages, not JSON APIs (a JSON login API is the ROPC grant removed by OAuth 2.1). Self-registration added behind a configuration flag | Stakeholder request ("if required"), architect decision |
| 21 | Docker testing | Automated end-to-end tool plus a manual browser guide | Stakeholder request |

## 3. Phase 5 scope as requested

Hardening items:
- DPoP (RFC 9449) sender-constrained tokens for the mobile client.
- PAR (RFC 9126) pushed authorization requests.
- Back-channel logout, so logging out at Identity ends BFF sessions.
- Token exchange (RFC 8693) for service-to-service calls on behalf of a user.
- Azure Key Vault for certificates and secrets, for when hosting moves to the cloud.
- Penetration test / OWASP ZAP baseline.

Gaps carried over from phases 1–4:
- Service clients and the BFF used client secrets instead of `private_key_jwt`.
- Removing 2FA or a passkey did not require a fresh sign-in.
- No password reset or email confirmation (no email sender).
- Permission cache and BFF refresh lock were per instance; scale-out needed Redis and a distributed lock.
- No introspection client registered for the API.
- No business endpoints (courses, grades) — explicitly deferred.
- No CI/CD pipeline (build, tests, migrator image, `--script-out` review gate).
- Artifactory returned 403 on package restore; packages were restored from nuget.org manually.

All items except business endpoints were delivered.

## 4. Working constraints and conventions

- Nothing is committed or pushed unless the stakeholder asks. All work is uncommitted on `dev`.
- Code, comments and documentation are written in plain professional English.
- No secrets in the repository: `.env` and `certs/` are git-ignored; `.env.example` holds placeholders only.
- Development certificates are generated locally (`tools/New-DevCertificates.ps1`); production keys come from Key Vault.
- Applications never run DDL and never migrate at startup; only `EduEco.Database` changes the schema.
- Released migration scripts are immutable (hash manifest test); changes use new scripts and expand/contract.
- Every API endpoint must carry authorization metadata (enforced by a test).
- Warnings are errors; NuGet audit is enabled at the lowest severity.
- Third-party GitHub Actions are pinned to commit SHAs.

## 5. Timeline

| Phase | Outcome |
|---|---|
| Planning | Standards baseline, architecture and phased plan agreed; open items (provider, hosting, federation, ORM, migrations, keys) resolved before coding |
| 1 — Foundation | Central package management, layered libraries, Dapper repositories with tenant/audit/concurrency guards, `AuthDbContext`, DbUp migrator with staged scripts, manifest guard, local Docker |
| 2 — Authorization server | OpenIddict server, Identity UI (login, 2FA, passkeys, tenant picker), token model, rate limiting, audit events |
| 3 — Resource server | JwtBearer, permission/scope/tenant handlers, resource-based checks, problem details, cached permission service, OpenAPI |
| 4 — BFF | YARP + OIDC, server-side encrypted sessions, CSRF header, refresh handling, logout |
| 5 — Hardening | `private_key_jwt`, PAR, DPoP, token exchange, introspection, back-channel logout, step-up, password reset / email confirmation, Redis scale-out, SQL refresh lock, Key Vault, ZAP fixes, GitHub Actions |

## 6. Problems met and how they were resolved

| Problem | Resolution |
|---|---|
| Artifactory 403 during `dotnet restore` | Repository `nuget.config` (`<clear/>`, nuget.org, package source mapping); also copied into Docker builds |
| Mixed IdentityModel versions (Protocols 7.x with Tokens 8.x) broke JWT handling | Pinned all `Microsoft.IdentityModel.*` / `System.IdentityModel.Tokens.Jwt` to one version with transitive pinning |
| A SQL comment inside entity metadata swallowed a comma | Removed the comment from generated SQL |
| `MERGE` against a join CTE was not allowed | Rewrote as `DELETE` + `INSERT` |
| Manifest guard failed after adding a migration | Regenerated the manifest entry with `print-manifest` (process documented) |
| ASP0016 on a minimal-API lambda discarded the result | Used a two-parameter lambda |
| `WWW-Authenticate` formatting (comma after scheme) | Fixed header builder |
| OpenIddict does not block reuse of a client assertion | Added `ClientAssertionReplayHandler` (single-use `jti`, lifetime ≤ 5 min) |
| OpenIddict has no DPoP support | Implemented proof validation and token binding; `cnf` injected into the access token principal by a sign-in handler |
| OpenIddict strips unknown claims such as `cnf` | Stored the thumbprint in a private claim and re-added `cnf` during sign-in processing |
| OpenIddict `Claims.Actor` is `actor`, RFC 8693 requires `act` | Used `act` explicitly |
| DPoP metadata missing from discovery | Registered the configuration handler earlier in the pipeline |
| API could not find the introspection endpoint | Read the typed `IntrospectionEndpoint`, falling back to additional metadata (same fix for the BFF revocation endpoint) |
| Fake test clock made client assertions look future-dated | Assertions and logout-token validation use wall-clock time; session logic keeps the injectable clock |
| Identity crashed in Docker with Redis configured (HybridCache missing for the invalidation listener) | Identity uses only the replay cache; the listener is registered only where HybridCache exists |
| Hosts crashed when Redis was unreachable at startup | `abortConnect=false`, background subscription with retry, best-effort invalidation publishing |
| Migrator resolved relative certificate paths against the working directory | Relative `PublicKeyCertificatePath` resolves against the migrator content root |
| `dotnet run` applied the Development launch profile in CI | CI uses `--no-launch-profile` |
| ZAP: CSP without `form-action`, missing COOP/COEP/CORP and Permissions-Policy | Headers added to Identity and BFF; client origins configurable through `IdentityServer:FormActionOrigins` |
| Token exchange with a DPoP-bound subject token failed with HTTP 500 (OpenIddict ID2196: only `cnf.x5t#S256` is understood). Found by the Docker end-to-end tool | A `ValidateTokenContext` handler disables OpenIddict's certificate-only proof-of-possession check for `cnf.jkt` tokens; DPoP is enforced by resource servers and the token endpoint. Regression test added |
| Heredoc quoting issues in the automation shell | Scripts written to files before execution (tooling only, no product impact) |

## 7. Current state

**Verified**
- 157 automated tests pass: Database 26, Infrastructure 21, Identity 52, Api 33, Bff 25.
- Docker end-to-end tool: 35/35 checks, repeatable (`EduEco/Tools/EduEco.AuthE2E`).
- Release build clean; no vulnerable packages reported.
- Docker stack smoke test: discovery (PAR, DPoP, back-channel logout, token exchange advertised), `private_key_jwt`
  client credentials, assertion replay rejected, API call succeeds, BFF login redirects through PAR, password reset
  email delivered to Mailpit.
- OWASP ZAP baseline: zero open warnings on Identity, BFF and API.
- `actionlint`: no findings.

**Not yet exercised**
- GitHub Actions workflows (nothing pushed yet).
- Key Vault against a real Azure vault (unit tests only).
- Browser sign-in through the Docker stack after the header changes (covered by integration tests, not by a manual browser run).

## 8. Key files

| Area | Location |
|---|---|
| Plan | `documents/Implementations/EduEco-Identity-Authentication-Authorization-Implementation-Plan.md` |
| Docker test guide | `documents/Implementations/EduEco-Identity-Authentication-Authorization-Docker-Test-Guide.md` |
| End-to-end tool | `EduEco/Tools/EduEco.AuthE2E` |
| Host setup | `EduEco/EduEco.Identity/Hosting/IdentityServerSetup.cs`, `EduEco/EduEco.Api/Hosting/ApiSetup.cs`, `EduEco/EduEco.Bff/Hosting/BffSetup.cs` |
| OAuth endpoints | `EduEco/EduEco.Identity/Controllers/AuthorizationController.cs` |
| Back-channel logout | `EduEco/EduEco.Identity/Logout/BackchannelLogout.cs`, `EduEco/EduEco.Bff/Endpoints/BackchannelLogoutEndpoint.cs` |
| Security primitives | `EduEco/Infrastructure/Security/` (`KeyMaterial`, `ClientAssertion`, `DPoP`, `ReplayCache`) |
| Authorization | `EduEco/EduEco.Api/Security/`, `EduEco/Infrastructure/Authorization/` |
| DI registration | `EduEco/ServiceRegistry/` (incl. `KeyVaultExtensions.cs`, `SecurityServicesExtensions.cs`) |
| BFF sessions and tokens | `EduEco/EduEco.Bff/Sessions/SqlSessionStore.cs`, `EduEco/EduEco.Bff/Tokens/` |
| Migrations and seeding | `EduEco/EduEco.Database/Scripts/`, `EduEco/EduEco.Database/Seeders/` |
| Local stack | `docker-compose.yml`, `.env.example`, `tools/New-DevCertificates.ps1`, `tools/New-ClientAssertion.ps1` |
| CI/CD | `.github/workflows/` (`ci.yml`, `codeql.yml`, `db-migrate.yml`, `zap-baseline.yml`), `.github/dependabot.yml`, `.zap/rules.tsv` |
| Component documentation | `README.md` and `EduEco/EduEco.{Identity,Api,Bff,Database}/README.md` |

## 9. Resuming the work

1. Review the uncommitted changes on `dev` and commit when approved.
2. Push and watch the first `ci.yml` and `codeql.yml` runs; adjust if hosted runners differ from local results.
3. Create GitHub environments `staging-plan`, `staging`, `production-plan`, `production` with
   `MIGRATOR_CONNECTION_STRING` secrets and required reviewers before using `db-migrate.yml`.
4. When cloud hosting starts: provision Key Vault (certificates, secrets, Data Protection key), Blob Storage for the
   key ring, managed identities, Redis; set `KeyVault:*` and `DataProtection:*`.
5. Next functional scope: business endpoints (courses, grades) on top of the existing permissions and scopes;
   optional external federation (school SSO, Entra ID, Google).
