# EduEco — Angular Registration & Profile: Implementation Plan

Records what was built and why: a new Angular SPA with self-service registration (invite-gated) and a profile page,
backed by a new `EduEco.Api` gateway that talks to `EduEco.Identity` server-to-server instead of the browser
redirecting there directly. Superseded/left untouched: `EduEco.Bff`'s cookie-session flow, which remains the pattern
for any app that doesn't need this gateway.

Related: [Docker test guide](EduEco-Angular-Registration-Profile-Docker-Test-Guide.md) ·
[Prior auth implementation plan](EduEco-Identity-Authentication-Authorization-Implementation-Plan.md)

---

## Context

The existing stack already has a full browser-redirect auth flow (BFF → Identity, PKCE, cookie session — see the
prior implementation plan). That flow deliberately keeps credentials out of the SPA entirely. This feature needed a
different shape: a real Angular-rendered Registration page (email/phone + password) and a Profile page, which means
the SPA *does* collect credentials and *does* need a JSON API to call — a new pattern for this codebase, built as a
thin, additive layer rather than a replacement.

## Decisions locked in during design

1. **Angular talks only to `EduEco.Api`.** Register/login/refresh/forgot-password all go through `EduEco.Api`, which
   calls `EduEco.Identity` server-to-server. Angular never calls Identity directly and never goes through the BFF.
2. **Phone is store-only, no OTP.** No SMS provider exists or was added. Email stays the mandatory login identifier;
   phone is an optional contact field on the account.
3. **Login uses ROPC** (`grant_type=password`), newly enabled but scoped to exactly one confidential,
   server-to-server-only OAuth client (`eduEco-api-ropc`). Every other client is unaffected.
4. **Session delivery is JSON tokens**, not a cookie. Api returns access/refresh tokens in the response body; Angular
   keeps them in memory only (never `localStorage`/`sessionStorage`) — a deliberate, documented step down from the
   BFF's httpOnly-cookie model in exchange for a simpler, API-first SPA. A hard page reload signs the user out.
5. **Tenant assignment is invite-code based.** A tenant admin issues a single-use, expiring code; registration
   redeems it. No self-created tenants.
6. **Profile is one row per user**, not per tenant membership — a person's name/DOB/address doesn't change per
   school they belong to.

## Phase map

| Phase | What | Key files |
|---|---|---|
| 1 | Identity: enable ROPC for one client; new internal JSON registration/password-reset endpoints | `EduEco.Identity/Hosting/IdentityServerSetup.cs`, `EduEco.Identity/Controllers/{InternalRegistrationController,InternalPasswordResetController}.cs`, `EduEco.Identity/Controllers/AuthorizationController.cs` (`ExchangePasswordAsync`) |
| 2 | Invite-code system (issue / validate / consume) | `EduEco.Database/Scripts/01_Migrations/V0008__tenant_invites.sql`, `Core/Authorization/TenantInvite.cs`, `Application/Invites/*`, `Infrastructure/Queries/InviteQueries.cs` |
| 3 | Profile entity + `api/v1/profile` | `EduEco.Database/Scripts/01_Migrations/V0009__user_profiles.sql`, `Core/Identity/UserProfile.cs`, `Application/Profiles/*`, `EduEco.Api/Controllers/ProfileController.cs` |
| 4 | Api gateway: `api/v1/auth/*` + invite issuance | `EduEco.Api/Security/IdentityInternalClient.cs`, `EduEco.Api/Controllers/{AuthController,InvitesController}.cs`, CORS + tighter rate limit in `ApiSetup.cs` |
| 5 | Angular SPA scaffold | `Web/edu-eco-app/` (Angular 20, standalone components) |
| 6 | End-to-end scenario | `Tools/EduEco.AuthE2E/Scenarios.cs` (section J) |

### Phase 1 — Identity: ROPC + internal endpoints

- `IdentityServerSetup.cs`: `.AllowPasswordFlow()` added to the OpenIddict server; a small pre-rate-limiter middleware
  reads `grant_type` from the `/connect/token` POST body so password-grant requests share the tight `login:{ip}`
  bucket (10/min) instead of the loose `token:{ip}` bucket meant for refresh/client-credentials traffic.
- `OpenIddictClientSeeder.cs`: added a `password` grant-type case, confidential-clients-only.
- New scope `identity.internal` / resource `eduEco-identity-internal` (`Core/Authorization/Scopes.cs`), granted only
  to the `eduEco-api` client's new `client_credentials` permission.
- `AuthorizationController.ExchangePasswordAsync`: validates credentials via `SignInManager.CheckPasswordSignInAsync`,
  resolves the tenant via the existing `TenantAccessResolver`, and — since there's no interactive UI here — rejects
  (same generic error, no enumeration) 2FA-required and multi-tenant accounts; those must still use the browser flow.
- `InternalRegistrationController` / `InternalPasswordResetController`: JSON mirrors of the existing
  `Pages/Account/{Register,ForgotPassword}` Razor logic, gated by a new `IdentityInternalScopePolicy`. Authentication
  uses OpenIddict's **local validation** (`UseLocalServer()`), not `JwtBearer` — Identity validating its own
  self-issued tokens over HTTP discovery breaks inside a WebApplicationFactory TestServer (no real network listener),
  and is also an unnecessary round-trip in production; local validation is in-process and works identically
  everywhere.

### Phase 2 — Invite-code system

- `auth.TenantInvites`: hashed code (SHA-256, plaintext shown once at issuance), `RoleId`, `ExpiresAtUtc`,
  `MaxUses`/`UseCount`, `RedeemedByUserId`/`RedeemedAtUtc`, `RowVersion`.
- `InviteService.IssueAsync` (tenant-scoped, admin action) / `ValidateAsync` (read-only, **cross-tenant by design** —
  redemption happens before the caller has any tenant context) / `ConsumeAsync` (marks redeemed; optimistic
  concurrency via `RowVersion` protects against two people racing the same single-use code).
- **Key wrinkle, resolved in Phase 4**: `ConsumeAsync` and the membership insert both go through the tenant-aware
  `ICommandRepository<T>`, which throws outside a valid tenant context. There is no tenant context at all for an
  anonymous registration request. Phase 4's `AuthController` builds a throwaway repository pinned to the redeemed
  invite's `TenantId` for exactly this one write, instead of using the normal per-request DI-injected repository.

### Phase 3 — Profile

- `dbo.UserProfiles`, keyed by `UserId` only (no `TenantId` — confirmed one profile per user). Fields: DateOfBirth,
  Address, City, PostalCode, Country.
- `ProfileService` always resolves `UserId` from `ICurrentUser` (the token), never from a route/query parameter —
  the standard BOLA guard already used by `MembershipsController`.

### Phase 4 — Api gateway

- `IdentityInternalClient`: reuses the existing `JwtBearerOptions.Backchannel`/`ConfigurationManager` (the same
  dev-certificate-trust and discovery wiring already proven for token validation) rather than standing up a new
  HttpClient — Identity's base address and TLS trust are configured in exactly one place.
- `AuthController` (`api/v1/auth/{register,login,refresh,forgot-password}`, all anonymous):
  - `register` validates the invite → calls Identity → **only on real account creation** attaches the membership and
    consumes the invite in one local transaction. The response is identical whether the email was new or already
    existed (no enumeration), matching the guarantee the existing Razor registration page already makes.
  - `login`/`refresh` relay the ROPC/refresh-token exchange; a failure never distinguishes "unknown user" from
    "wrong password".
- `InvitesController` (`api/v1/invites`, `[HasPermission(Permissions.Users.Manage)]`): issues a code, returned in
  plaintext exactly once.
- New for this solution: CORS (`Authentication:AllowedSpaOrigins`, bearer-only — no `AllowCredentials`) and a
  dedicated, tighter rate-limit bucket (`AuthEndpointPermitsPerMinute`, default 10/min) for the anonymous endpoints.

### Phase 5 — Angular SPA

`Web/edu-eco-app/` — Angular 20, standalone components, typed reactive forms, no NgModules, no SSR, no state library.

```
src/app/
  core/
    config.ts                 API base URL (single hardcoded dev origin; add real env files if a second target appears)
    auth/
      token-store.ts          access/refresh tokens in memory only (Angular signal)
      auth-api.ts             register / login / refresh / forgot-password HTTP calls
      auth.interceptor.ts     attaches Authorization: Bearer; one silent refresh-and-retry on 401
      auth.guard.ts           gates /profile on TokenStore.isAuthenticated()
    profile/
      profile-api.ts          GET/PUT api/v1/profile
  features/
    login/login.page.ts
    register/register.page.ts
    profile/profile.page.ts
```

Routes: `/login` (default), `/register`, `/profile` (guarded). Each page is a standalone, lazy-loaded component.

### Phase 6 — End-to-end scenario

`Tools/EduEco.AuthE2E/Scenarios.cs`, section J: issue invite (API) → register (API) → reuse rejected → confirm email
(Mailpit link) → login (ROPC via API) → update/read the caller's own profile (API) → refresh (API). Runs against the
live Docker stack alongside the existing sections A–I (OIDC/DPoP/BFF flows), which stayed green throughout.

## Cross-cutting risks carried forward (not fully closed)

- **ROPC client certificate**: `eduEco-api-ropc` reuses the existing `api-client.cer`/`.pfx` (same private key,
  different client id) rather than a new certificate — simplest for now; the upgrade path if isolation matters later
  is a dedicated cert.
- **Concurrent refresh from two tabs** can trip OpenIddict's refresh-token reuse-detection and force a surprise
  logout (observed directly in the E2E run's section H4, which exercises this deliberately for the mobile/DPoP flow).
  No special handling was added for the Angular/ROPC refresh path.
- **Profile fields** (DateOfBirth/Address/City/PostalCode/Country) reflect what was confirmed during design; extend
  the migration if the real requirement grows.
