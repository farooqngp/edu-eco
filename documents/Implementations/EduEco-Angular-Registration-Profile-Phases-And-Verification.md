# EduEco — Angular Registration & Profile: 6 Phases and Docker Verification

Angular SPA registers users through invite codes and calls a new `EduEco.Api` gateway instead of redirecting to
Identity directly. This doc lists the 6 implementation phases and how to verify each one on the local Docker stack.

Related: [Implementation plan](EduEco-Angular-Registration-Profile-Implementation-Plan.md) (design decisions, why) ·
[Full test guide](EduEco-Angular-Registration-Profile-Docker-Test-Guide.md) (setup, manual browser walkthrough)

---

## The 6 phases

| # | Phase | Key files |
|---|---|---|
| 1 | Identity: enable ROPC for one client; internal JSON registration/password-reset endpoints | `EduEco.Identity/Hosting/IdentityServerSetup.cs`, `Controllers/{InternalRegistrationController,InternalPasswordResetController,AuthorizationController}.cs` |
| 2 | Invite-code system (issue / validate / consume) | `V0008__tenant_invites.sql`, `Core/Authorization/TenantInvite.cs`, `Application/Invites/*` |
| 3 | Profile entity + `api/v1/profile` | `V0009__user_profiles.sql`, `Core/Identity/UserProfile.cs`, `Application/Profiles/*`, `EduEco.Api/Controllers/ProfileController.cs` |
| 4 | Api gateway: `api/v1/auth/*` + invite issuance | `EduEco.Api/Security/IdentityInternalClient.cs`, `Controllers/{AuthController,InvitesController}.cs`, CORS + rate limit in `ApiSetup.cs` |
| 5 | Angular SPA scaffold | `Web/edu-eco-app/` (Angular 20, standalone components) |
| 6 | End-to-end scenario | `Tools/EduEco.AuthE2E/Scenarios.cs` (section J) |

## Verify from Docker

### 0. Start the stack

```bash
docker compose up -d --build
docker compose ps
```

Expect `sqlserver`, `redis`, `mailpit`, `identity`, `api`, `bff` running; `db-migrator` exited 0.

### Phase 1 — ROPC + internal endpoints

```bash
docker compose logs db-migrator | grep -E "identity.internal|eduEco-api-ropc"
curl -sk https://localhost:7013/.well-known/openid-configuration | grep -o '"password"'
```

Expect the scope/client lines in the migrator log, and `"password"` present in `grant_types_supported`.

### Phase 2 — Invite-code system

```bash
docker compose logs db-migrator | grep V0008
```

Expect `V0008__tenant_invites.sql` executed. Functional check happens through Phase 4/6 (issuing and redeeming a
code) — there's no standalone endpoint to hit before Phase 4 exists.

### Phase 3 — Profile

```bash
docker compose logs db-migrator | grep V0009
```

Expect `V0009__user_profiles.sql` executed. Functional check via Phase 4/5 (`GET`/`PUT api/v1/profile`).

### Phase 4 — Api auth gateway

```bash
curl -sk -X POST https://localhost:7037/api/v1/auth/forgot-password \
  -H "Content-Type: application/json" -d '{"email":"nobody@example.com"}' -w "\n%{http_code}\n"
```

Expect `202` regardless of whether the email exists (no enumeration).

### Phase 5 — Angular SPA

```bash
cd Web/edu-eco-app
npm install   # first time only
npm start     # http://localhost:4200
```

Open http://localhost:4200 — redirects to `/login`. Submit the register form with a bogus invite code → expect
**"Invalid invite code."** rendered inline (proves the SPA reaches the API through CORS). Navigate directly to
`/profile` while signed out → expect an immediate redirect to `/login` (route guard).

For the full registration → confirm → login → save-profile walkthrough (needs a real invite code, issued by a tenant
admin token), see the [full test guide, section 5](EduEco-Angular-Registration-Profile-Docker-Test-Guide.md#5-manual-browser-checks).

### Phase 6 — End-to-end scenario

```bash
dotnet run --project EduEco/Tools/EduEco.AuthE2E
```

Expect **41/41 checks passed**. Section **J** (new) exercises Phases 1–4 together in one run:

| Check | Proves |
|---|---|
| J1 | Tenant admin issues a single-use invite (`POST api/v1/invites`) |
| J2 | `POST api/v1/auth/register` with that invite creates the account; confirmation email lands in Mailpit |
| J3 | The same invite code is rejected on a second registration (single-use enforced) |
| J4 | The emailed confirmation link confirms the account |
| J5 | `POST api/v1/auth/login` (ROPC) issues tokens; `PUT`/`GET api/v1/profile` round-trips the caller's own data |
| J6 | `POST api/v1/auth/refresh` issues a new access token |

Sections A–I (unchanged, pre-existing OIDC/DPoP/BFF flows) must stay green too — a regression there means Phase 1's
ROPC/rate-limit changes broke something else.

### Tear down

```bash
docker compose down        # keep the SQL volume
docker compose down -v     # also drop it
```
