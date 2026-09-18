# EduEco.Bff — Backend for Frontend

Serves the SPA and is its only backend origin. Tokens stay on the server (IETF `draft-ietf-oauth-browser-based-apps`).
The browser holds one opaque, `__Host-` prefixed, HttpOnly, Secure, SameSite=Strict cookie.

## Flows

```
Browser → GET /bff/login[?returnUrl=/path][&tenant=code]
        → BFF pushes the request to Identity /connect/par (private_key_jwt) → request_uri
        → 302 Identity /connect/authorize?client_id&request_uri (PKCE, state, nonce, tenant stay on the back channel)
        → user signs in at Identity
        → 302 /signin-oidc (code) → BFF redeems code (private_key_jwt client assertion, back channel)
        → tokens stored in bff.Sessions, cookie = session key → 302 returnUrl

Browser → GET /api/** (cookie + X-CSRF: 1)
        → BFF: CSRF header? session? access token fresh (refresh if not)?
        → YARP forwards to API with Authorization: Bearer …, Cookie header removed

Browser → GET /bff/logout?sid=… → revoke refresh token → delete session → Identity end session

Identity → POST /bff/backchannel-logout (logout+jwt) → every session of that user deleted (all devices)
```

## Endpoints

| Route | Purpose |
|---|---|
| `GET /bff/login` | Start sign-in. `returnUrl` must be a local path; `tenant` picks the tenant at Identity |
| `GET /bff/user` | `sub`, name, email, `tenantId`, session expiry, `logoutUrl`. 401 when signed out. Requires `X-CSRF: 1` |
| `GET /bff/logout?sid=` | `sid` comes from `/bff/user` (same-origin only) → no cross-site logout |
| `POST /bff/backchannel-logout` | OIDC Back-Channel Logout receiver. Validates signature (issuer JWKS, key-rollover retry), `iss`, `aud`, `typ=logout+jwt`, `exp`, `events`, no `nonce`, `sub`, single-use `jti`. Otherwise 400 `invalid_request` |
| `/api/**` | Proxied to EduEco.Api with the user's access token |
| `/health/live` | Liveness |

## Why the browser cannot be attacked with these tokens

| Threat | Control |
|---|---|
| Token theft via XSS | No token in JS reachable storage. Cookie is HttpOnly |
| CSRF | SameSite=Strict cookie **and** required `X-CSRF: 1` header (forces a blocked CORS preflight cross-site) |
| Session replay after logout | Session row is deleted; a replayed cookie authenticates nothing |
| Token leak from the DB | Tickets and tokens encrypted with Data Protection; cookie key stored only as SHA-256 |
| Browser sending its own `Authorization`/cookies to the API | Both headers are stripped by the proxy transform |
| Open redirect | `returnUrl` must be a local path (`/x`, not `//host`, `/\host`, absolute) |

## Refresh handling

- Access tokens are refreshed when less than `AccessTokenRefreshThreshold` (60 s) remains.
- Refresh is serialised per session: Identity uses one-time refresh tokens and revokes the chain on reuse.
- Failed refresh (expired/revoked/reused) deletes the session and returns 401, so the SPA can sign in again.
- Multiple BFF instances: an in-process gate plus a SQL Server application lock (`sp_getapplock`, per session) serialise
  refreshes cluster-wide. No affinity or extra infrastructure is needed. On lock timeout (15 s) the still-valid token is used.

## Configuration (`Bff` section)

| Key | Notes |
|---|---|
| `Authority` | Identity issuer URL |
| `ClientId` | Confidential client (`eduEco-bff`) |
| `ClientAssertion` | `private_key_jwt`: `{ CertificatePath, CertificatePassword }` or `{ KeyVaultName }`. Required in Production |
| `ClientSecret` | Development fallback only, when no `ClientAssertion` is configured |
| `UsePushedAuthorization` | PAR (default `true`, required) |
| `ApiBaseAddress` | Resource server base URL |
| `Scopes` | `openid profile email offline_access api.read api.write` |
| `SessionLifetime` | Absolute, non-sliding (8 h) |
| `AccessTokenRefreshThreshold` | 60 s |
| `SessionCleanupInterval` | Expired-session sweep (15 min) |
| `DataProtectionKeysPath` | Shared key ring; Production: this or `DataProtection:BlobUri` (+ `KeyVaultKeyId`) |
| `RequireHttpsMetadata`, `AllowUntrustedCertificates` | Development only for the latter |

Sessions live in `bff.Sessions` (migration `V0007`).

## Local run

```bash
docker compose up -d --build bff
```

SPA: `https://localhost:7100` (sign in with a seeded dev user). Identity: 7013. API: 7037.

## Production checklist

- [ ] Same-origin deployment: SPA, `/bff/**` and `/api/**` under one host
- [ ] Shared Data Protection key ring across instances (else sessions break on restart/scale-out)
- [ ] `ClientAssertion:KeyVaultName` in the cloud; public key registered through the migrator seed
- [ ] `/bff/backchannel-logout` reachable from the Identity server (internal network)
- [ ] Session table growth watched; cleanup service running
