# EduEco.Api — OAuth 2.0 resource server

Validates EduEco.Identity access tokens locally (JWKS) and authorizes every request by **scope + permission + tenant**.

## Request pipeline

```
ExceptionHandler → HSTS/HTTPS → security headers
→ Authentication (JwtBearer: iss, aud=eduEco-api, typ=at+jwt, exp ±30s, signature via JWKS)
→ RateLimiter (per client_id+sub, else IP)
→ TenantStatusMiddleware (tenant_id well-formed and tenant active)
→ Authorization (fallback = authenticated; [HasPermission] → PermissionAuthorizationHandler; resource checks)
→ Controllers (tenant always from token → ITenantContext → Dapper repositories/queries)
```

## Authorization model

`[HasPermission(Permissions.X.Y)]` evaluates, in order:

1. **Scope** — the token must hold the permission's `RequiredScope` (`api.read` / `api.write`). Missing → `403` + `WWW-Authenticate: Bearer error="insufficient_scope", scope="…"`.
2. **Tenant** — tenant-scoped permissions need a `tenant_id` claim. Missing → `403 tenant_required`.
3. **Service clients** (`sub == client_id`) — scope-only, and only permissions marked `AllowServiceClients`. Else `403 service_client_not_allowed`.
4. **Users** — `IPermissionService` resolves permissions from the database for *(user, tenant)*: tenant memberships + global roles. **Role claims in the token are ignored** (forged/stale roles grant nothing). Else `403 permission_denied`.

Resource-based checks (`SameTenantRequirement`) back up tenant-filtered queries; foreign resources return `404`, never `403`.

| Permission | Scope | Service clients | Tenant-scoped |
|---|---|---|---|
| `tenants.read` | api.read | ✓ | ✓ |
| `tenants.manage` | api.write | ✗ | ✗ |
| `users.manage` | api.write | ✗ | ✓ |
| `courses.read` / `grades.read` | api.read | ✓ | ✓ |
| `courses.write` / `grades.write` | api.write | ✓ | ✓ |

Source of truth: `EduEco.Core.Authorization.Permissions` / `Roles`, mirrored by DbUp reference data (verified by tests).

## Caching and revocation

| Change | Takes effect |
|---|---|
| Membership added/removed through this API | Next request (cache tag eviction) |
| Change made elsewhere (other instance, DB) | ≤ `AuthorizationCache:PermissionCacheDuration` (5 min) |
| Tenant deactivated | ≤ `AuthorizationCache:TenantStatusCacheDuration` (1 min) |
| User deactivated / password or 2FA changed | Refresh refused immediately at Identity; issued access tokens expire ≤ 10 min |
| Token revoked (logout, password reset, reuse detection) | Immediately for `[RequireActiveToken]` endpoints (introspection, 30 s cache); other endpoints at token expiry |

Multi-instance: with `ConnectionStrings:Redis`, permission evictions are broadcast over Redis pub/sub
(`eduEco:cache-invalidation`), so a membership change on one instance reaches all instances at once. Publishing is best effort;
the cache TTL bounds staleness while Redis is down. Instances start and keep serving when Redis is unavailable.

## Sender-constrained tokens (DPoP, RFC 9449)

- `Authorization: DPoP <token>` + `DPoP: <proof>` header. The proof must match method, URL (no query), `ath` (token hash) and the
  token `cnf.jkt`; `iat` within 60 s; `jti` single-use (Redis-backed replay cache across instances).
- A DPoP-bound token sent as `Bearer` is rejected (no downgrade). An unbound token sent with the `DPoP` scheme is rejected.
- Challenges: `WWW-Authenticate: DPoP error="invalid_dpop_proof", algs="ES256 ES384 PS256 RS256"`.

## Introspection and token exchange

- `[RequireActiveToken]` (on membership POST/DELETE) calls `/connect/introspect` as client `eduEco-api` with a `private_key_jwt`
  assertion. It fails closed: a revoked or unknown token gets `401 invalid_token` with problem `code=token_revoked`.
- The same client may exchange a user access token (RFC 8693) for a `reporting.read` token to `eduEco-reporting`
  (downstream call on behalf of the user; token carries `act.sub = eduEco-api`).

## Errors

RFC 9457 `application/problem+json` with `code` and `traceId`. `401` codes: `token_required`, `invalid_token`, `token_revoked`. `403` codes: `insufficient_scope`, `tenant_required`, `service_client_not_allowed`, `permission_denied`, `tenant_inactive`. Validation details in `WWW-Authenticate` only in Development.

## Endpoints

| Method | Route | Requires |
|---|---|---|
| GET | `/api/v1/me` | authenticated |
| GET | `/api/v1/tenants/current` | `tenants.read` |
| GET | `/api/v1/tenants` | `tenants.manage` |
| GET/POST | `/api/v1/memberships` | `users.manage` |
| GET/DELETE | `/api/v1/memberships/{id}` | `users.manage` |
| GET | `/health/live` | anonymous |
| GET | `/openapi/v1.json`, `/scalar` | anonymous, Development only |

New endpoints are protected by the fallback policy; a test fails if any `api/*` endpoint lacks `[Authorize]`/`[HasPermission]` or if a new anonymous endpoint appears.

## Configuration

| Key | Default | Notes |
|---|---|---|
| `Authentication:Authority` | — | Issuer URL (must equal token `iss`) |
| `Authentication:MetadataAddress` | — | Internal discovery URL if the issuer URL is not routable |
| `Authentication:Audience` | `eduEco-api` | |
| `Authentication:ClockSkew` | 30 s | |
| `Authentication:PermitsPerMinute` | 600 | Per caller |
| `Authentication:AllowUntrustedBackchannelCertificate` | false | Development only (refused otherwise) |
| `AuthorizationCache:*` | 5 min / 1 min | `00:00:00` disables caching |
| `Authentication:Introspection:Enabled` | false | Enables `[RequireActiveToken]` checks |
| `Authentication:Introspection:ClientId` | `eduEco-api` | |
| `Authentication:Introspection:CertificatePath` / `CertificatePassword` / `CertificateKeyVaultName` | — | `private_key_jwt` key |
| `Authentication:Introspection:CacheDuration` | 30 s | Upper bound on revocation delay |
| `ConnectionStrings:Redis` | — | Scale-out: L2 cache, invalidation bus, DPoP replay cache |
| `DPoP:*` | 60 s / 5 s | Proof lifetime, clock skew, algorithms |
| `KeyVault:*` | — | See EduEco.Identity README (Azure Key Vault) |

## Local run

```bash
docker compose up -d --build api
```

API: `https://localhost:7037/scalar` (sign in with a seeded dev user; client `eduEco-api-docs`).
