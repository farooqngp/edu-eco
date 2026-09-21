# EduEco — Angular Registration & Profile: Docker Test Guide

How to test the invite-gated registration/login/profile feature: an automated end-to-end run (41 checks, extending
the existing 35) followed by manual checks in the Angular app itself.

Related: [Implementation plan](EduEco-Angular-Registration-Profile-Implementation-Plan.md) ·
[Prior test guide](EduEco-Identity-Authentication-Authorization-Docker-Test-Guide.md) (general stack setup, 2FA/passkeys)

---

## 1. Prerequisites

Same as the [prior guide](EduEco-Identity-Authentication-Authorization-Docker-Test-Guide.md#1-prerequisites), plus:

| Tool | Notes |
|---|---|
| Node.js | 22.12+ (Angular 20 requires `^20.19 \|\| ^22.12 \|\| ^24`) |
| npm | ships with Node |

Free local port: 4200 (Angular dev server), in addition to 7013/7037/7100/8025/14333.

## 2. Start the backend stack

From the repository root (`.env` and `./certs` already set up per the prior guide):

```bash
docker compose up -d --build
docker compose ps
```

Expected: `sqlserver`, `redis`, `mailpit`, `identity`, `api`, `bff` running; `db-migrator` exited 0. Check its log for
the new migrations:

```bash
docker compose logs db-migrator | grep -E "V0008|V0009|identity.internal|eduEco-api-ropc"
```

Expected lines: `V0008__tenant_invites.sql`, `V0009__user_profiles.sql` executed; `Scope identity.internal upserted`;
`Client eduEco-api-ropc (confidential, private_key_jwt) upserted`.

## 3. Automated end-to-end run

```bash
dotnet run --project EduEco/Tools/EduEco.AuthE2E
```

Expect **41/41 checks passed**. Section **J** is the new one:

| Check | What it proves |
|---|---|
| J1 | A tenant admin can issue a single-use invite through `POST api/v1/invites` |
| J2 | `POST api/v1/auth/register` with that invite creates the account and a confirmation email lands in Mailpit |
| J3 | The same invite code is rejected on a second registration attempt (single-use enforced) |
| J4 | The emailed confirmation link actually confirms the account |
| J5 | `POST api/v1/auth/login` (ROPC) issues tokens; `PUT`/`GET api/v1/profile` round-trips the caller's own data |
| J6 | `POST api/v1/auth/refresh` issues a new access token |

If J1 fails with `token_revoked`, something upstream (e.g. a modified section H) revoked the token this section
reuses — J1 mints its own fresh admin sign-in specifically to avoid that; a failure here means a real regression, not
test ordering.

## 4. Start the Angular app

```bash
cd Web/edu-eco-app
npm install   # first time only
npm start     # ng serve, http://localhost:4200
```

The app calls `https://localhost:7037` directly (see `src/app/core/config.ts`); CORS for `http://localhost:4200` is
already enabled on the `api` container via `Authentication__AllowedSpaOrigins__0` in `docker-compose.yml`. If you
change the Angular dev port, update that setting and restart `api`.

## 5. Manual browser checks

### 5.1 Get a real invite code

There's no admin UI yet (Phase 4 only added the API). Issue one directly:

1. Sign in as the seeded tenant admin (`tenant.admin@demo.eduEco.local`, password = `DEV_USER_PASSWORD` from `.env`)
   through the API reference at https://localhost:7037/scalar, or reuse a token obtained via the E2E tool's own flow.
2. `POST https://localhost:7037/api/v1/invites` with `{ "roleName": "Student", "expiresAtUtc": "<a future ISO date>" }`
   and that admin's bearer token.
3. Copy the `code` from the response — it's shown once and never persisted in plaintext.

### 5.2 Register

1. Open http://localhost:4200 — redirects to `/login`.
2. Click **Create an account**.
3. Fill in the invite code, an email, a display name, and a password (12+ characters). Submit.
4. Expect: *"Check your email to confirm your address, then sign in."*
5. Open http://localhost:8025 (Mailpit), find the confirmation email, open the link. Expect: *"Your email address is
   confirmed."*

### 5.3 Error paths (no invite code needed)

- Register with an obviously invalid invite code (e.g. `not-a-real-code`) → expect **"Invalid invite code."** shown
  inline on the form.
- Log in with a made-up email/password → expect **"The username or password is incorrect."**
- Navigate directly to http://localhost:4200/profile while signed out → expect an immediate redirect to `/login`
  (the route guard).

### 5.4 Login and profile

1. On `/login`, sign in with the email/password from 5.2 (after confirming the email).
2. Expect redirect to `/profile`.
3. Fill in date of birth / address / city / postal code / country, click **Save**. Expect *"Saved."* and the fields
   to persist across a re-fetch (reload the page — note this also signs you out, since the access token lives in
   memory only; sign in again and confirm the saved values are still there).

## 6. Tear down

```bash
docker compose down          # keep the SQL volume
docker compose down -v       # also drop it (next `up` re-seeds from scratch)
```

Stop the Angular dev server with `Ctrl+C`.
