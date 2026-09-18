# EduEco — Authentication and Authorization: Docker Test Guide

How to test the complete authentication and authorization implementation on the local Docker stack: an automated
end-to-end run (35 checks) followed by the manual browser checks that need a human (2FA, passkeys, visual UI).

Related: [Implementation plan](EduEco-Identity-Authentication-Authorization-Implementation-Plan.md) ·
[Context](EduEco-Identity-Authentication-Authorization-Context.md)

---

## 1. Prerequisites

| Tool | Notes |
|---|---|
| Docker Desktop | Running, Linux containers |
| .NET 10 SDK | Version from `EduEco/global.json` |
| PowerShell | Windows PowerShell 5.1 or PowerShell 7 |
| Browser | Chrome or Edge (passkeys need Windows Hello or a security key) |

Free local ports: 7013 (Identity), 7037 (API), 7100 (BFF/SPA), 8025 (Mailpit), 14333 (SQL Server).

## 2. One-time setup

Run from the repository root.

1. Create `.env` from the template and set every value (strong, unique values; the file is git-ignored):

   ```powershell
   Copy-Item .env.example .env
   notepad .env
   ```

   | Variable | Used for |
   |---|---|
   | `MSSQL_SA_PASSWORD` | SQL Server `sa` login (container only) |
   | `DEV_USER_PASSWORD` | Password of the seeded development users (min. 12 characters) |
   | `IDENTITY_CERT_PASSWORD` | Protects every development PFX in `./certs` |

2. Generate development certificates (token signing/encryption, HTTPS, `private_key_jwt` client keys):

   ```powershell
   powershell -ExecutionPolicy Bypass -File tools/New-DevCertificates.ps1
   ```

3. Trust the ASP.NET Core HTTPS development certificate in the browser (once per machine):

   ```powershell
   dotnet dev-certs https --trust
   ```

## 3. Start the stack

```bash
docker compose up -d --build
docker compose ps
```

Expected: `sqlserver`, `redis`, `mailpit`, `identity`, `api`, `bff` running; `db-migrator` exited with code 0.

Health checks:

```powershell
curl.exe -sk https://localhost:7013/health/live
curl.exe -sk https://localhost:7037/health/live
curl.exe -sk https://localhost:7100/health/live
```

Each prints `Healthy`.

| URL | Purpose |
|---|---|
| https://localhost:7100 | SPA through the BFF |
| https://localhost:7013/.well-known/openid-configuration | Identity discovery document |
| https://localhost:7013/Account/Login | Identity sign-in page (links to register / forgot password) |
| https://localhost:7037/scalar | API reference with OAuth sign-in (Development) |
| http://localhost:8025 | Mailpit inbox (confirmation and reset emails) |

## 4. Test accounts and clients

Seeded by the migrator (`--seed-dev-users`); password = `DEV_USER_PASSWORD` from `.env`.

| User | Role | Tenant |
|---|---|---|
| `platform.admin@eduEco.local` | PlatformAdmin (global) | any active tenant (tenant picker) |
| `tenant.admin@demo.eduEco.local` | TenantAdmin | `demo-school` |
| `teacher@demo.eduEco.local` | Teacher | `demo-school` |
| `student@demo.eduEco.local` | Student | `demo-school` |

| Client | Type | Authentication | Notes |
|---|---|---|---|
| `eduEco-bff` | confidential | `private_key_jwt` (`certs/bff-client.pfx`) | PAR required, back-channel logout |
| `eduEco-mobile` | public | PKCE | DPoP required; redirect `com.eduEco.mobile:/oauth2redirect` |
| `eduEco-api-docs` | public | PKCE | Scalar UI |
| `eduEco-api` | confidential | `private_key_jwt` (`certs/api-client.pfx`) | introspection + token exchange |
| `eduEco-svc-dev` | confidential | `private_key_jwt` (`certs/svc-dev-client.pfx`) | client credentials, tenant `demo-school` |

## 5. Automated end-to-end test (recommended first)

```bash
dotnet run --project EduEco/Tools/EduEco.AuthE2E
```

The tool reads `.env` and `./certs`, drives the real endpoints like a browser, mobile app, service and resource
server, and reads emails from Mailpit. Exit code `0` means every check passed. It is safe to re-run (each run registers
a new throwaway user and removes the membership it creates). Development only: it trusts self-signed certificates on
localhost.

| Group | Checks |
|---|---|
| A. Discovery | PAR endpoint, DPoP algorithms, token exchange, `private_key_jwt`, back-channel logout, S256-only PKCE, no legacy grants; JWKS without private material |
| B. Service client | No client authentication rejected; guessed secret rejected; signed assertion issues an `at+jwt` for `eduEco-api`; replayed assertion rejected; `/me` identifies the service caller and tenant; user-only endpoint refused; missing token 401 with `WWW-Authenticate`; tampered signature 401 |
| C. Mobile app | Authorization without PKCE rejected; code redemption without DPoP proof rejected; tenant admin gets a DPoP-bound token (`token_type=DPoP`, `cnf.jkt`); API accepts it with a proof, rejects it as Bearer, rejects a replayed proof |
| D. Permissions | Tenant admin lists memberships; teacher gets `403 permission_denied` |
| E. Resource server as client | Token exchange returns a `reporting.read` token for `eduEco-reporting` with `act`; introspection reports the token active |
| F. Registration | Register → confirmation email → confirm → `access_denied` without tenant → tenant admin grants Student membership through the API → user signs in with Student role |
| G. Forgot password | Reset email → new password; old password refused; new password works |
| H. Revocation | Revoked token still reads (signature valid) but sensitive write returns `401 token_revoked`; refresh rotation keeps DPoP binding, refresh without proof or with another key rejected; reused refresh token rejected |
| I. BFF | Login uses PAR (URL has only `client_id` + `request_uri`); `/bff/user` requires `X-CSRF`; proxied API call carries the user's token; only the `__Host-EduEco.Bff` cookie reaches the browser; logout in one browser ends the session in another browser (back-channel logout) |

Expected tail of the output:

```
  PASS I3  Logout in one browser ends the user's BFF session in another browser (back-channel logout)
         tablet session ended after 0.5 s

35/35 checks passed
```

Options: `--identity`, `--api`, `--bff`, `--mailpit` override the default URLs.

## 6. Manual browser tests

Use a normal window and a private window to simulate two devices.

### 6.1 SPA sign-in through the BFF
1. Open https://localhost:7100 and choose **Sign in**. Sign in as `teacher@demo.eduEco.local`.
2. Developer tools → Application → Cookies (`localhost:7100`): only `__Host-EduEco.Bff` (HttpOnly, Secure, SameSite=Strict). Local/session storage hold no tokens.
3. Developer tools → Network: the address bar during sign-in shows `/connect/authorize?client_id=eduEco-bff&request_uri=urn:...` (PAR); no `code_challenge` or scopes in the URL.
4. Call the API from the SPA: requests go to `https://localhost:7100/api/...` with the `X-CSRF: 1` header; no `Authorization` header in the browser.

### 6.2 Registration and onboarding
1. https://localhost:7013/Account/Login → **Create an account**. Use a new address and a 12+ character password.
2. Open http://localhost:8025, open **Confirm your EduEco email address**, click the link → "Your email address is confirmed".
3. Sign in through the SPA with the new account → you return to the SPA with a login error: the account has no tenant access (`access_denied`).
4. Grant access as tenant admin: https://localhost:7037/scalar → authorize with `tenant.admin@demo.eduEco.local` → `POST /api/v1/memberships` with `{ "userId": <id from the confirmation link>, "role": "Student" }` → 201.
5. Sign in again with the new account → success; `GET /api/v1/me` shows role `Student`.
6. Registering an existing address shows the same "Check your email" page and sends nothing (no account enumeration).

### 6.3 Forgot password
1. Sign-in page → **Forgot your password?** → enter the address → generic confirmation.
2. Mailpit → **Reset your EduEco password** → set a new password.
3. Every session of that user ends (SPA session in both windows returns to signed-out; back-channel logout).
4. Old password fails; new password works. Unknown addresses get the same generic page and no email.

### 6.4 Two-factor authentication and step-up
1. Sign in at https://localhost:7013/Account/Manage → **Two-factor authentication** → scan the QR code with an authenticator app, confirm, save recovery codes.
2. Sign out and in again → TOTP code required; a recovery code also works once.
3. Wait more than 5 minutes after sign-in, then try to disable 2FA → redirected to **Confirm your password** (step-up re-authentication). After confirming, the change is allowed.

### 6.5 Passkeys
1. Account → **Passkeys** → **Add passkey** (Windows Hello / security key). Step-up applies after 5 minutes, as above.
2. Sign out → **Sign in with a passkey** → signed in without a password.

### 6.6 Lockout
Enter a wrong password 5 times for one account → lockout page; the account stays locked for 15 minutes (or until a
password reset, which clears the lockout).

### 6.7 Tenant picker
Sign in through the SPA as `platform.admin@eduEco.local` → tenant picker lists active tenants; the chosen tenant becomes
the `tenant_id` of the token. `https://localhost:7100/bff/login?tenant=demo-school` skips the picker.

### 6.8 Single logout across devices
1. Sign in as the same user in the normal and the private window.
2. Sign out in one window (SPA → **Sign out**).
3. Refresh the other window → signed out (Identity sent `logout+jwt` to `/bff/backchannel-logout`).

## 7. Manual API and token calls (PowerShell)

Service token with `private_key_jwt` (assertions are single-use and valid 60 s):

```powershell
$assertion = ./tools/New-ClientAssertion.ps1 -ClientId eduEco-svc-dev
$token = (curl.exe -sk -X POST https://localhost:7013/connect/token `
  -d grant_type=client_credentials -d client_id=eduEco-svc-dev -d scope=api.read `
  -d "client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer" `
  -d "client_assertion=$assertion" | ConvertFrom-Json).access_token
curl.exe -sk https://localhost:7037/api/v1/me -H "Authorization: Bearer $token"
```

Expected security responses:

| Request | Expected |
|---|---|
| Same assertion sent twice | `invalid_client` "The client assertion has already been used." |
| `GET /api/v1/me` without token | `401`, `WWW-Authenticate: Bearer` |
| `GET /api/v1/memberships` with the service token | `403` problem details (`insufficient_scope` / `service_client_not_allowed`) |
| `GET /connect/authorize?...` without `code_challenge` | `invalid_request` |
| `grant_type=password` | `unsupported_grant_type` |

The `.http` files in `EduEco/EduEco.Identity` and `EduEco/EduEco.Api` contain the same calls for Visual Studio / VS Code.

## 8. Observing the system

| What | How |
|---|---|
| Audit events | `docker compose logs identity \| Select-String AUDIT` (EventIds 5000–5099); BFF: `docker compose logs bff \| Select-String AUDIT` |
| Emails | http://localhost:8025 |
| Replay cache entries (DPoP / client assertions) | `docker compose exec redis redis-cli --scan --pattern "eduEco:replay:*"` |
| BFF sessions | `SELECT SessionId, Subject, ExpiresAtUtc FROM bff.Sessions` (SQL Server on `localhost,14333`) |
| Security headers | `curl.exe -skI https://localhost:7013/Account/Login` (CSP, COOP/COEP/CORP, Permissions-Policy) |
| OWASP ZAP baseline | See `.github/workflows/zap-baseline.yml`; locally: `docker run --rm --network container:edueco-identity-1 zaproxy/zap-stable zap-baseline.py -t https://localhost:7013/ -a` |

## 9. Troubleshooting

| Symptom | Fix |
|---|---|
| `429 Too Many Requests` on sign-in pages | Local limit is 60 login-type POSTs per minute per IP (`IdentityServer__RateLimits__LoginPermitsPerMinute`); wait a minute |
| Browser warns about the certificate | `dotnet dev-certs https --trust`, then regenerate `certs/aspnetcore-https.pfx` with `tools/New-DevCertificates.ps1 -Force` and rebuild |
| E2E: `.env not found` / certificate errors | Run from inside the repository; complete section 2 |
| No email in Mailpit | `docker compose logs identity \| Select-String -Pattern "Email\|SMTP"`; check `Email__Smtp__Host=mailpit` |
| `db-migrator` exited non-zero | `docker compose logs db-migrator`; certificates must exist before the migrator seeds client keys |
| Back-channel logout does not end the other session | `docker compose logs identity \| Select-String "logout"`; the BFF must be reachable at `https://localhost:7100` from the Identity container |
| Start from an empty database | **Warning:** `docker compose down -v` permanently deletes the SQL Server volume, all users and all sessions. Then `docker compose up -d --build` |

## 10. Why there is no JSON "login" or "register" API

- **Login** happens only on the Identity server's pages (`/Account/Login`), reached through the authorization code flow.
  A JSON endpoint that accepts a username and password is the Resource Owner Password Credentials grant, which OAuth 2.1
  and RFC 9700 remove: it exposes passwords to client applications, cannot support 2FA, passkeys or lockout-aware UX,
  and trains users to type credentials into any app.
- **Register** and **Forgot password** are Identity server pages as well (`/Account/Register`, `/Account/ForgotPassword`),
  protected by anti-forgery tokens and rate limiting, and they never reveal whether an address has an account.
  Self-registration is off by default (`IdentityServer:AllowSelfRegistration`) and enabled only in the Docker/Development
  configuration; new accounts need email confirmation and a tenant membership granted by an administrator.
- Applications (SPA, mobile) link to these pages; the BFF and mobile app then receive tokens through the standard flow.
