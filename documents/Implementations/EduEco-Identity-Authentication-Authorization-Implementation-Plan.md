# EduEco — Identity, Authentication and Authorization Implementation Plan

| | |
|---|---|
| **Scope** | OAuth 2.1 / OpenID Connect authentication and permission-based, tenant-aware authorization for the EduEco platform |
| **Status** | Phases 1–5 implemented (branch `dev`). Business endpoints (courses, grades) deliberately out of scope |
| **Platform** | .NET 10, OpenIddict 7, ASP.NET Core Identity, SQL Server, Dapper / Dapper.Contrib, DbUp, YARP, Redis |
| **Last updated** | 2026-09-18 |

---

## 1. Context and goals

The starting point was a blank solution template: `EduEco.Api` and `EduEco.Identity` were WeatherForecast scaffolds, the
class libraries were empty and `EduEco.Database` was a console stub.

Goals:

- `EduEco.Identity` becomes the OpenID Connect authorization server for every client type.
- `EduEco.Api` becomes a resource server with permission-based, tenant-aware authorization.
- Browser clients never hold tokens (Backend-for-Frontend).
- Database schema is owned by versioned, reviewable migrations suitable for production change control.
- The design follows current industry standards (OAuth 2.1, RFC 9700 Security BCP, OWASP API Top 10).

## 2. Decisions

| Area | Decision |
|---|---|
| Authorization server | OpenIddict (self-hosted, no external federation for now) |
| Clients | SPA through a BFF, native mobile app, service-to-service |
| Tenancy | Multi-tenant (schools / districts); one tenant per access token |
| Authorization model | RBAC + fine-grained permissions, resolved server-side (not in tokens) |
| Database | SQL Server |
| Hosting | Local Docker now; Azure later (Key Vault wiring prepared) |
| Domain data access | Dapper + Dapper.Contrib: generic repository for commands, Dapper query layer for reads (CQRS style) |
| Identity data access | Hybrid: EF Core used only internally by ASP.NET Core Identity and OpenIddict stores, **no EF migrations** |
| Schema changes | DbUp owns 100 % of DDL (`EduEco.Database`), with a production migration runbook |
| Primary keys | `bigint IDENTITY` everywhere (clustered, sequential, high-volume friendly); no GUID keys |
| CI/CD | GitHub Actions (`github.com/farooqngp/edu-eco`) |
| NuGet | Repository `nuget.config`: nuget.org only, package source mapping |
| Email | SMTP (MailKit); Mailpit for local development |

## 3. Standards baseline

| Area | Standard / rule |
|---|---|
| Protocol | OAuth 2.1 draft, RFC 9700 (OAuth 2.0 Security BCP), OpenID Connect Core |
| Flows | Authorization code + PKCE (S256 only) for SPA/BFF and mobile; client credentials for services. Implicit, hybrid and ROPC disabled |
| SPA | BFF pattern (IETF `draft-ietf-oauth-browser-based-apps`): tokens server-side, `__Host-` cookie, SameSite=Strict, anti-CSRF header |
| Mobile | RFC 8252 (system browser, PKCE), refresh token rotation, DPoP (RFC 9449) sender-constrained tokens |
| Client authentication | `private_key_jwt` (RFC 7523) for all confidential clients; secrets refused in Production |
| Authorization requests | PAR (RFC 9126), required for the BFF |
| Access tokens | JWT profile RFC 9068 (`typ=at+jwt`), RS256, 10 min, `aud=eduEco-api` |
| Refresh tokens | Encrypted, rotating, one-time use, reuse detection revokes the chain; 14 d sliding / 30 d absolute |
| Delegation | Token exchange (RFC 8693) with `act` claim |
| Revocation / state | RFC 7009 revocation, RFC 7662 introspection, OIDC Back-Channel Logout 1.0 |
| Errors | RFC 9457 problem details, RFC 6750 `WWW-Authenticate` |
| Keys | Asymmetric certificates (Docker: mounted PFX; cloud: Azure Key Vault); rotation through JWKS overlap |
| User authentication | ASP.NET Core Identity: passwords, lockout, TOTP 2FA, recovery codes, passkeys (WebAuthn), step-up re-authentication |
| API security | OWASP API Top 10: BOLA handled by resource-based checks and tenant filters; rate limiting |
| Database change management | Forward-only, versioned, immutable scripts; expand/contract; migrator separate from applications |

## 4. Target architecture

```
SPA ──cookie──> EduEco.Bff (YARP + OIDC, PAR, private_key_jwt) ──Bearer──────┐
Mobile ──code + PKCE + DPoP──> EduEco.Identity (OpenIddict) ─────────────────┼─> EduEco.Api (JwtBearer, DPoP, permissions)
Service ──client_credentials (private_key_jwt)──> EduEco.Identity ───────────┘
EduEco.Identity ──back-channel logout (logout+jwt)──> EduEco.Bff
EduEco.Api ──introspection / token exchange (private_key_jwt)──> EduEco.Identity

Identity + OpenIddict stores ─> AuthDbContext (EF Core, schema "auth", no migrations)
Domain data (tenants, permissions, memberships) ─> Dapper (commands: Contrib repository | queries: query executor)
BFF sessions ─> bff.Sessions (Dapper, encrypted)
Schema ─> EduEco.Database (DbUp) — the only component that runs DDL
Redis ─> HybridCache L2, cache-invalidation pub/sub, replay cache (DPoP / client assertion / logout jti)
```

### 4.1 Solution layering

| Project | Responsibility |
|---|---|
| `Core` | Entities, constants (`Permissions`, `Roles`, `Scopes`, `Resources`, `EduEcoClaimTypes`, client properties), marker interfaces |
| `Application` | Persistence and security abstractions (`ICommandRepository<T>`, `IUnitOfWork`, `IQueryExecutor`, `ICurrentUser`, `ITenantContext`, `IPermissionService`), feature query interfaces |
| `Infrastructure` | Dapper repositories and query executor, `AuthDbContext`, permission service, cache-invalidation bus, security primitives (`KeyMaterial`, `ClientAssertion`, `DPoP`, `ReplayCache`) |
| `ServiceRegistry` | DI extension methods (persistence, auth stores, authorization, distributed cache, DPoP, Key Vault); host `Program.cs` files stay thin |
| `EduEco.Identity` | Authorization server, Razor Pages account UI |
| `EduEco.Api` | Resource server |
| `EduEco.Bff` | Backend-for-Frontend for the SPA |
| `EduEco.Database` | DbUp migrator and OAuth client / dev user seeders |

References: Application → Core; Infrastructure → Application; ServiceRegistry → Infrastructure; hosts → ServiceRegistry.

## 5. Phases

### Phase 1 — Foundation

**Build hygiene**
- `Directory.Build.props`: net10.0, nullable, implicit usings, `TreatWarningsAsErrors`, latest analyzers, NuGet audit (all, low).
- `Directory.Packages.props`: central package management with transitive pinning (IdentityModel pinned to one version family).
- `global.json`: SDK pin and Microsoft.Testing.Platform runner.

**Core / Application / Infrastructure**
- Marker interfaces: `IEntity` (`long Id`), `ITenantOwned`, `IAuditable`, `IConcurrencyAware` (`RowVersion`).
- Entities: `Tenant`, `Permission`, `RolePermission`, `UserTenantMembership` (Dapper); `ApplicationUser : IdentityUser<long>`, `ApplicationRole : IdentityRole<long>` (EF-managed).
- `DapperCommandRepository<T>` (open generic) over Dapper.Contrib with:
  - tenant guard (`ITenantOwned`: tenant stamped on insert, verified on get/update/delete; cross-tenant access behaves as not found),
  - audit stamping (`IAuditable`),
  - optimistic concurrency (`IConcurrencyAware` → `ConcurrencyException`).
- `DapperUnitOfWork` (scoped connection + transaction), `DapperQueryExecutor` (read connection, `CommandDefinition`, paging with `OFFSET/FETCH`), read-replica-ready connection factory.
- `AuthDbContext : IdentityDbContext<…, long>` with `HasDefaultSchema("auth")` and OpenIddict entities; used only by the stores.

**EduEco.Database (DbUp migrator)**

```
Scripts/
  00_PreDeploy/     RunAlways   P0001__environment_checks.sql
  01_Migrations/    RunOnce     V0001__create_schemas.sql
                                V0002__auth_identity_tables.sql
                                V0003__auth_openiddict_tables.sql
                                V0004__tenants.sql
                                V0005__permissions_memberships.sql
                                V0006__auth_user_passkeys.sql
                                V0007__bff_sessions.sql
  02_ReferenceData/ RunAlways   R0001__roles.sql, R0002__permissions.sql, R0003__role_permissions.sql
  03_PostDeploy/    RunAlways   S0001__app_role_grants.sql
  99_Dev/           RunOnce     D0001__demo_tenant.sql (Development only)
```

- Journal: `migration.SchemaVersions`; transaction per script; embedded resources; ordered by folder and name.
- CLI: `migrate` (default), `--dry-run`, `--script-out <file>`, `--ensure-db` (local/CI only), `--seed-clients`, `--seed-dev-users` (Development only), `print-manifest`, `generate-auth-ddl`.
- Immutability guard: `Scripts/manifest.json` hash manifest; a test fails if a released V-script changes.
- Reference data (roles, permissions, role-permission map) mirrors the `Core` constants; tests verify the mirror.
- Application logins get DML only (role `eduEco_app`); the migrator uses a DDL login.

**Local Docker**: `sqlserver` (healthcheck) → `db-migrator` (one-off job) → hosts.

### Phase 2 — Authorization server (`EduEco.Identity`)

- ASP.NET Core Identity: lockout, confirmed email required, TOTP 2FA, recovery codes, passkeys; `__Host-` cookies; 8 h absolute session.
- OpenIddict server endpoints: authorize, token, userinfo, revoke, introspect, end session, discovery, JWKS.
- Flows: authorization code + PKCE (S256 required), refresh token, client credentials.
- Access tokens signed (not encrypted), `typ=at+jwt`, audience `eduEco-api`, 10 min.
- Credentials: `Certificate` mode (PFX, required in Production), `Development`, `Ephemeral` (tests). Data Protection key ring persisted and encrypted.
- `AuthorizationController` + Razor Pages: login, 2FA, passkeys, tenant picker, logout confirmation; first-party clients skip consent.
- Claims: `sub`, `client_id`, `aud`, `scope`, `tenant_id`, `role`. **One tenant per token**; switching tenant means a new token. Service clients get `tenant_id` from the client registration.
- Every code/refresh redemption re-validates: user active and confirmed, security stamp unchanged, tenant access still valid, roles re-read.
- Rate limiting on token and login endpoints, forwarded headers, security headers, audit events (EventId 5000–5099).
- Token pruning background service.

### Phase 3 — Resource server (`EduEco.Api`)

- JwtBearer: authority, audience `eduEco-api`, `ValidTypes = at+jwt`, no inbound claim mapping, 30 s clock skew.
- Pipeline: authentication → tenant resolution → authorization; fallback policy = authenticated user.
- `[HasPermission]` evaluates, in order:
  1. **Scope** — token holds the permission's required scope (`api.read` / `api.write`), else `403 insufficient_scope`.
  2. **Tenant** — tenant-scoped permissions need `tenant_id`, else `403 tenant_required`.
  3. **Service clients** — scope-only, and only permissions marked `AllowServiceClients`, else `403 service_client_not_allowed`.
  4. **Users** — `PermissionService` resolves permissions from the database for (user, tenant); **role claims in the token are ignored**, else `403 permission_denied`.
- Resource-based checks (BOLA): foreign-tenant resources return `404`, never `403`.
- `HttpCurrentUser` / `HttpTenantContext` feed the repository tenant guards.
- Permission cache: `HybridCache` (5 min) with tag eviction on membership change; tenant status cache (1 min).
- RFC 9457 problem details with stable `code` values; OpenAPI with OAuth2 security scheme; Scalar UI in Development.
- Endpoints: `/api/v1/me`, `/api/v1/tenants/current`, `/api/v1/tenants`, `/api/v1/memberships` (CRUD). A test fails if any endpoint lacks authorization metadata.

**Roles and permissions**

| Role | Scope of role |
|---|---|
| `PlatformAdmin` | All tenants (global) |
| `TenantAdmin` | Membership-scoped |
| `Teacher`, `Student`, `Parent` | Membership-scoped |

| Permission | Required scope | Service clients | Tenant-scoped |
|---|---|---|---|
| `tenants.read` | api.read | yes | yes |
| `tenants.manage` | api.write | no | no |
| `users.manage` | api.write | no | yes |
| `courses.read` / `grades.read` | api.read | yes | yes |
| `courses.write` / `grades.write` | api.write | yes | yes |

### Phase 4 — Backend-for-Frontend (`EduEco.Bff`)

- YARP reverse proxy for `/api/**` plus ASP.NET Core OpenID Connect handler (confidential client, code + PKCE).
- Browser holds one opaque `__Host-EduEco.Bff` cookie (HttpOnly, Secure, SameSite=Strict).
- Server-side sessions in `bff.Sessions`: cookie key stored as SHA-256; ticket and tokens encrypted with Data Protection; deleting the row ends the session immediately.
- Anti-CSRF: `X-CSRF: 1` header required on data requests; logout requires the session id (`sid`) that only `/bff/user` exposes.
- Proxy strips browser `Cookie` / `Authorization` and injects the user's access token.
- Refresh before expiry (60 s threshold); failed refresh ends the session.
- Endpoints: `/bff/login`, `/bff/user`, `/bff/logout`, `/api/**`.

### Phase 5 — Hardening and carried-over gaps

| Item | Implementation |
|---|---|
| `private_key_jwt` | `ClientAssertion` helper (typ `client-authentication+jwt`, 1 min). Seeder registers the public key (JWKS) from a certificate. Identity enforces `jti` + `exp`, lifetime ≤ 5 min and **single use** (`ClientAssertionReplayHandler`). Used by the BFF (code, PAR, refresh, revoke), the API (introspection, token exchange) and service clients. Seeder refuses secrets in Production |
| PAR (RFC 9126) | `/connect/par`; per-client requirement flag; BFF uses `PushedAuthorizationBehavior.Require` |
| DPoP (RFC 9449) | Custom proof validator (typ, alg, public JWK, signature, `htm`, `htu`, `iat` window, `ath`, `jkt`, `jti` replay). Token endpoint binds tokens (`cnf.jkt`, `token_type=DPoP`); refresh tokens stay bound. API accepts `DPoP` scheme, rejects bearer downgrade and unbound DPoP use |
| Back-channel logout | Identity queues a global logout (end session, password reset): revokes authorizations and tokens, POSTs signed `logout+jwt` to each client `backchannel_logout_uri` with retries. BFF receiver validates the token fully and deletes all sessions of the subject |
| Token exchange (RFC 8693) | `eduEco-api` exchanges a user token (must be an audience) for `reporting.read` on `eduEco-reporting`; `act.sub` added; lifetime capped by the subject token |
| Introspection client | `eduEco-api` registered for introspection; `[RequireActiveToken]` on sensitive writes; fails closed with `401 token_revoked`; 30 s cache |
| Step-up re-authentication | `[RequireRecentAuthentication]` on 2FA and passkey management; `/Account/Reauthenticate`; 5 min window |
| Password reset / email confirmation | Forgot/reset password (generic responses, 30 min token, lockout reset, global logout), confirm and resend email. MailKit SMTP sender; Mailpit in Docker |
| Self-registration | `/Account/Register` behind `IdentityServer:AllowSelfRegistration` (off by default). No account enumeration; email confirmation required; no token until a tenant administrator adds a membership |
| Docker end-to-end test | `EduEco/Tools/EduEco.AuthE2E`: 35 checks against the running stack (see the Docker test guide) |
| Scale-out | Redis: HybridCache L2, permission invalidation over pub/sub, replay cache. BFF refresh serialised cluster-wide with SQL Server `sp_getapplock`. Hosts start and run while Redis is unavailable |
| Azure Key Vault | `KeyVault:Uri` → secrets as configuration (optional prefix), certificates by `KeyVaultName` (Identity signing/encryption, API and BFF client keys), Data Protection key ring in Blob Storage wrapped by a Key Vault key. Inert without configuration |
| Security headers (ZAP findings) | CSP with `form-action 'self' <FormActionOrigins>`, COOP / COEP / CORP, Permissions-Policy (WebAuthn self only), SPA shell `no-cache` |
| OWASP ZAP baseline | Clean on Identity, BFF and API; accepted informational alerts documented in `.zap/rules.tsv` |
| CI/CD | GitHub Actions (see section 9) |
| Artifactory 403 | Repository `nuget.config` (nuget.org only, source mapping), also copied into Docker builds |

## 6. Client registrations

Seeded by `EduEco.Database --seed-clients` from `Seed:Clients`.

| client_id | Type | Grants | Client authentication | Scopes / resources | Extras |
|---|---|---|---|---|---|
| `eduEco-bff` | confidential web | authorization_code, refresh_token | `private_key_jwt` | openid profile email offline_access api.read api.write | PAR required; back-channel logout URI |
| `eduEco-mobile` | public native | authorization_code, refresh_token | none (PKCE) | openid profile email offline_access api.read api.write | DPoP required |
| `eduEco-api-docs` | public web (Development) | authorization_code | none (PKCE) | openid api.read api.write | Scalar UI |
| `eduEco-api` | confidential | token exchange | `private_key_jwt` | reporting.read → `eduEco-reporting` | introspection allowed |
| `eduEco-svc-dev` | confidential | client_credentials | `private_key_jwt` | api.read api.sync | bound to tenant `demo-school` |

## 7. Token model

| Token | Format / lifetime | Contents |
|---|---|---|
| Access | JWS `typ=at+jwt`, RS256, 10 min | `sub`, `client_id`, `aud`, `scope`, `tenant_id`, `role`; `cnf.jkt` when DPoP-bound; `act` after token exchange |
| Identity | JWT, 10 min | `sub`, `tenant_id`, profile/email by scope |
| Refresh | Encrypted, rotating, one-time use, 14 d sliding / 30 d absolute | Reuse revokes the chain; DPoP key binding preserved |
| Logout | `logout+jwt`, 2 min | `iss`, `aud`, `sub`, `jti`, back-channel logout event |

Permissions are never placed in tokens; the API resolves them per request (cached).

## 8. Revocation and consistency

| Change | Takes effect |
|---|---|
| Membership added/removed via API | Immediately on all instances (tag eviction + Redis pub/sub) |
| Change made directly in the database | ≤ permission cache TTL (5 min) |
| Tenant deactivated | ≤ tenant status cache TTL (1 min) |
| User deactivated, password or 2FA changed | Refresh refused immediately; issued access tokens expire ≤ 10 min |
| Logout / password reset | Tokens revoked; BFF sessions deleted via back-channel logout; `[RequireActiveToken]` endpoints refuse the token within 30 s |

## 9. CI/CD and database change process

| Workflow | Trigger | Steps |
|---|---|---|
| `ci.yml` | push / PR to `main`, `dev` | Restore (audit), build with warnings as errors, vulnerable-package gate, 5 test suites (matrix, TRX + Markdown summary), migration script artifact from an empty database, idempotency check, 4 images to GHCR with SBOM and signed provenance |
| `codeql.yml` | push / PR / weekly | CodeQL `security-extended` for C# and workflow files |
| `db-migrate.yml` | manual | **Plan** (environment `<env>-plan`): verify image attestation, `--dry-run`, `--script-out` artifact. **Apply** (environment `<env>` with required reviewers): same image runs `migrate` with the DDL login |
| `zap-baseline.yml` | weekly / manual / relevant PRs | Full compose stack with throwaway secrets and certificates; ZAP baseline on Identity, BFF and API |
| `dependabot.yml` | weekly | NuGet (grouped), GitHub Actions, Docker base images |

All third-party actions are pinned to commit SHAs. Applications never migrate at startup.

**Production database runbook**
1. CI produces the migrator image and the reviewable full script.
2. Plan job produces `pending.sql` against the target database.
3. DBA and release owner review; take a backup / point-in-time restore checkpoint.
4. Approve the apply job; the migrator runs with the DDL login (no `--ensure-db`, no dev seeding).
5. Deploy applications after exit code 0. Rollback is a forward fix; restore only for data-destructive failures.

## 10. Configuration reference (summary)

| Host | Key settings |
|---|---|
| Identity | `IdentityServer:Issuer`, `CredentialMode`, `SigningCertificates[]` / `EncryptionCertificates[]` (`Path`/`Password` or `KeyVaultName`), `DataProtectionKeysPath`, `FormActionOrigins[]`, `ReauthenticationWindow`, lifetimes, `RateLimits`, `KnownProxies[]`; `Email:Smtp:*`; `ConnectionStrings:Redis`; `DPoP:*` |
| API | `Authentication:Authority`, `Audience`, `ClockSkew`, `PermitsPerMinute`, `Introspection:*` (`Enabled`, `ClientId`, certificate or `CertificateKeyVaultName`, `CacheDuration`); `AuthorizationCache:*`; `ConnectionStrings:Redis`; `DPoP:*` |
| BFF | `Bff:Authority`, `ClientId`, `ClientAssertion` (certificate or `KeyVaultName`), `UsePushedAuthorization`, `ApiBaseAddress`, `Scopes`, `SessionLifetime`, `AccessTokenRefreshThreshold`, `DataProtectionKeysPath` |
| All hosts (cloud) | `KeyVault:Uri`, `ManagedIdentityClientId`, `LoadSecrets`, `SecretPrefix`; `DataProtection:BlobUri`, `KeyVaultKeyId` |
| Migrator | `ConnectionStrings:EduEco`, `Seed:Clients:<id>:*` (`PublicKeyCertificatePath`, `RequirePushedAuthorizationRequests`, `RequireDPoP`, `BackchannelLogoutUri`, `AllowIntrospection`, `Resources`, `TenantCode`), `Seed:DevUsers:*` |

Local development helpers: `tools/New-DevCertificates.ps1` (token, HTTPS and client certificates) and
`tools/New-ClientAssertion.ps1` (one-time assertion for manual token requests).

## 11. Verification

| Suite | Tests | Covers |
|---|---|---|
| `EduEco.Database.Tests` | 26 | Script manifest immutability, ordering/contiguity, reference data mirrors `Core` constants |
| `EduEco.Infrastructure.IntegrationTests` | 21 | DbUp from empty database, repository CRUD, tenant guard, concurrency, paging, `AuthDbContext` schema drift, Key Vault wiring |
| `EduEco.Identity.IntegrationTests` | 52 | Discovery, code + PKCE end to end, client credentials, refresh rotation and reuse detection, PKCE enforcement, `private_key_jwt` and replay, PAR, DPoP binding, token exchange, back-channel logout delivery, password reset, email confirmation, self-registration, step-up, exchange of DPoP-bound tokens |
| `EduEco.Api.IntegrationTests` | 33 | 401/403 matrix (audience, type, expiry, scope, permission, tenant, service clients), BOLA, DPoP (valid, downgrade, bad proofs, cross-instance replay), introspection revocation, cross-instance permission invalidation |
| `EduEco.Bff.IntegrationTests` | 25 | Login, opaque cookie, proxying, CSRF, refresh (including concurrent and cross-instance), logout and cookie replay, PAR + `private_key_jwt`, back-channel logout (valid, replay, 9 invalid token cases) |

Total: 157 tests, all passing. Docker end-to-end tool: 35/35 checks (`dotnet run --project EduEco/Tools/EduEco.AuthE2E`),
step-by-step instructions in [the Docker test guide](EduEco-Identity-Authentication-Authorization-Docker-Test-Guide.md).
OWASP ZAP baseline with zero open warnings; actionlint clean.

## 12. Production checklist

- [ ] Signing/encryption certificates in Key Vault (RSA ≥ 3072 or ECDSA P-256); rotation runbook (publish new key ≥ one access-token lifetime + cache TTL before switching)
- [ ] Shared Data Protection key ring for every host (Blob Storage + Key Vault key)
- [ ] `ConnectionStrings:Redis` for multi-instance deployments
- [ ] All confidential clients on `private_key_jwt` with keys in Key Vault; public keys registered through the migrator
- [ ] `FormActionOrigins` lists every client origin; exact redirect URIs only
- [ ] BFF back-channel logout endpoint reachable from Identity over the internal network
- [ ] TLS at a proxy listed in `KnownProxies`; HSTS enabled
- [ ] SMTP relay credentials in Key Vault; SPF/DKIM for the sender domain
- [ ] Least-privilege SQL logins: applications DML only (`eduEco_app`), migrator DDL
- [ ] GitHub environments `<env>-plan` and `<env>` with `MIGRATOR_CONNECTION_STRING` secrets and required reviewers
- [ ] Audit events (EventId 5000–5099 Identity, 6010–6011 BFF) shipped to the SIEM; alerts on lockout spikes, refresh token reuse and rejected logout tokens

## 13. Known limitations and next steps

- Business endpoints (courses, grades) are not implemented; their permissions and scopes already exist.
- Logout at the Identity server is global: it ends the user's sessions on every device and client.
- While Redis is unreachable, requests that need replay checks (DPoP, client assertions) fail closed with a server error.
- Key Vault integration is verified by unit tests only; validate against a real vault when cloud hosting starts.
- External identity federation (school SSO, Entra ID, Google) is out of scope for now.
