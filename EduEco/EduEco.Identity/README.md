# EduEco.Identity — OpenID Connect authorization server

OpenIddict 7 server + ASP.NET Core Identity (.NET 10). Issues tokens for `eduEco-api`; never serves business data.

## Endpoints

| Endpoint | Purpose |
|---|---|
| `/.well-known/openid-configuration`, `/.well-known/jwks` | Discovery, public signing keys |
| `/connect/par` | RFC 9126 pushed authorization requests (required for clients registered with `RequirePushedAuthorizationRequests`, e.g. the BFF) |
| `/connect/authorize` | Authorization code + PKCE (S256 only). Optional `tenant=<code>` parameter (also accepted in the pushed request) |
| `/connect/token` | `authorization_code`, `refresh_token`, `client_credentials`, `urn:ietf:params:oauth:grant-type:token-exchange` (RFC 8693) |
| `/connect/userinfo` | `sub`, `tenant_id`, profile/email by scope |
| `/connect/revoke`, `/connect/introspect` | RFC 7009 / RFC 7662 |
| `/connect/endsession` | RP-initiated logout (confirmation page + anti-forgery); triggers back-channel logout |
| `/Account/*` | Login, TOTP 2FA, recovery codes, passkeys, tenant picker, self-registration (`AllowSelfRegistration`), forgot/reset password, email confirmation, re-authentication |
| `/health/live` | Liveness |

Not enabled (OAuth 2.1 / RFC 9700): implicit, hybrid, password (ROPC), `plain` PKCE.

## Tokens

| Token | Format / lifetime | Contents |
|---|---|---|
| Access | JWS `typ=at+jwt` (RFC 9068), RS256, 10 min | `sub`, `client_id`, `aud=eduEco-api`, `scope`, `tenant_id`, `role` |
| Identity | JWT, 10 min | `sub`, `tenant_id`, name/email by scope |
| Refresh | Encrypted, rotating, one-time use, 14 d sliding / 30 d absolute | Reuse ⇒ whole chain revoked |

- **One tenant per token.** Members of one tenant: automatic. Several tenants: picker or `tenant=` parameter. PlatformAdmin: any active tenant.
- **Service clients** (`client_credentials`): `sub = client_id`; `tenant_id` only if the client registration has the `tenant_id` property (`Seed:Clients:<id>:TenantCode`).
- **Re-validated on every code/refresh redemption:** account active + email confirmed, security stamp unchanged (password/2FA/passkey changes revoke refresh), tenant access still valid, roles re-read.
- Permissions are **not** in tokens; the API resolves them server-side.

## Phase 5 hardening

| Feature | Behaviour |
|---|---|
| Client authentication | `private_key_jwt` (RFC 7523) for confidential clients; public key (JWKS) registered from `Seed:Clients:<id>:PublicKeyCertificatePath`. Assertions: `jti` + `exp` required, lifetime at most 5 min, **single use** (replay cache). The seeder refuses client secrets in Production |
| PAR (RFC 9126) | `/connect/par`; per-client requirement flag. The browser URL carries only `client_id` + `request_uri` |
| DPoP (RFC 9449) | `DPoP` proof at `/connect/token` gives an access token with `cnf.jkt` and `token_type=DPoP`. Required for clients with `RequireDPoP` (mobile); refresh tokens stay bound to the same key. Discovery advertises `dpop_signing_alg_values_supported` |
| Token exchange (RFC 8693) | A resource server exchanges a user access token (it must be an audience of it) for a down-scoped token to another resource. Adds `act: { sub: <client_id> }`; lifetime at most the remaining subject-token lifetime; user and tenant access re-checked |
| Back-channel logout | On end session, password reset or global logout, all authorizations and tokens of the user are revoked. Then a signed `logout+jwt` (`events`, `sub`, `jti`, 2 min) is POSTed to every client with a `BackchannelLogoutUri`. Retries after 1 s and 5 s |
| Account lifecycle | Forgot/reset password (generic response, 30 min token, lockout reset, global logout), email confirmation + resend. SMTP via MailKit (`Email:Smtp`), Mailpit in Docker |
| Step-up | Disabling 2FA and managing passkeys require a sign-in newer than `ReauthenticationWindow` (5 min), else `/Account/Reauthenticate` |
| Replay cache | `IReplayCache`: Redis (`ConnectionStrings:Redis`, cluster-wide) or in-memory (single instance) |
| Security headers | CSP incl. `form-action 'self' <FormActionOrigins>`, COOP/COEP/CORP, Permissions-Policy (WebAuthn for this origin only), `X-Frame-Options: DENY` |

## Configuration (`IdentityServer` section)

| Key | Notes |
|---|---|
| `Issuer` | Public URL clients see (behind proxy: external URL) |
| `CredentialMode` | `Certificate` (required in Production) · `Development` (dev cert store) · `Ephemeral` (tests) |
| `SigningCertificates[]`, `EncryptionCertificates[]` | `{ Path, Password }` or `{ KeyVaultName }`. First = active; keep previous cert listed during rotation so its key stays in JWKS |
| `DataProtectionKeysPath` | Persistent key ring (Production: this or `DataProtection:BlobUri`); encrypted with the first encryption certificate unless `DataProtection:KeyVaultKeyId` is set |
| `FormActionOrigins[]` | Client origins reached by redirects after login/consent/logout form posts (CSP `form-action`) |
| `ReauthenticationWindow` | Step-up window for 2FA/passkey changes (5 min) |
| `AllowSelfRegistration` | Enables `/Account/Register` (off by default; on in Docker/Development). New accounts need email confirmation and an administrator-granted tenant membership |
| `AllowUntrustedBackchannelLogoutCertificates` | Development only (Docker self-signed certificates) |
| `KnownProxies[]` | IPs allowed to send `X-Forwarded-*` |
| `RateLimits` | Per-IP POST limits for `/connect/token` and login pages |
| Lifetimes | `AccessTokenLifetime`, `RefreshTokenLifetime`, `RefreshTokenAbsoluteLifetime`, `RefreshTokenReuseLeeway` (0 = strict), `SessionLifetime` |

Other sections: `Email:Smtp` (Host, Port, Security `StartTls`/`SslOnConnect`/`None`, UserName, Password, FromAddress), `ConnectionStrings:Redis`,
`DPoP` (ProofLifetime, ClockSkew, algorithms), `KeyVault` / `DataProtection` (see below).

## Azure Key Vault (cloud hosting)

| Key | Effect |
|---|---|
| `KeyVault:Uri` | Enables Key Vault. Authentication: managed identity (`KeyVault:ManagedIdentityClientId` for user-assigned) or the default credential chain; no vault credentials in configuration |
| `KeyVault:LoadSecrets` / `SecretPrefix` | Vault secrets become configuration (`ConnectionStrings--EduEco` becomes `ConnectionStrings:EduEco`); the prefix lets hosts share a vault |
| `SigningCertificates[n]:KeyVaultName` | Certificate (with private key) downloaded from Key Vault at startup |
| `DataProtection:BlobUri`, `DataProtection:KeyVaultKeyId` | Key ring in Blob Storage, wrapped by a Key Vault key |

Without `KeyVault:Uri` nothing Azure-related runs (local Docker uses files). The API and BFF use the same settings.

## Local run

```bash
docker compose up -d sqlserver
docker compose run --rm --build db-migrator
dotnet run --project EduEco/EduEco.Identity
```

Needs a connection string password via user-secrets (`ConnectionStrings:EduEco`). Uses the OpenIddict development certificates.

Docker (HTTPS on `https://localhost:7013`):

```bash
powershell -ExecutionPolicy Bypass -File tools/New-DevCertificates.ps1
docker compose up -d --build identity
```

## Production checklist

- [ ] Signing/encryption certificates from Key Vault (`KeyVaultName`, RSA ≥ 3072 or ECDSA P-256), rotation runbook (add new → publish ≥ 1 access-token lifetime + cache TTL → switch active → remove old)
- [ ] Shared key ring across instances (`DataProtection:BlobUri` + `KeyVaultKeyId`, or `DataProtectionKeysPath`)
- [ ] `ConnectionStrings:Redis` set when running more than one instance (cluster-wide assertion/DPoP replay protection)
- [ ] SMTP relay credentials in Key Vault; SPF/DKIM for the sender domain
- [ ] `FormActionOrigins` lists every client origin
- [ ] Least-privilege SQL login in role `eduEco_app` (no DDL)
- [ ] TLS terminated at proxy listed in `KnownProxies`; HSTS on
- [ ] Only confidential BFF and native clients with exact redirect URIs; all confidential clients on `private_key_jwt` (enforced in Production)
- [ ] Ship `AUDIT` events (EventId 5000–5099) to the SIEM; alert on 5002 (lockout) spikes and 5031 (refresh reuse / rejected grants)
