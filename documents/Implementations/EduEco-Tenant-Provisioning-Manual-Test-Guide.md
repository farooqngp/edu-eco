# EduEco — Tenant Provisioning & Invite Email: Manual Test Guide

How to walk the whole chain by hand in the Angular app: a platform administrator registers a school, that school's
administrator receives an email and sets their own password, signs in to their dashboard, and invites a member who
receives the invite code by email and registers with it.

Related: [Angular registration/profile test guide](EduEco-Angular-Registration-Profile-Docker-Test-Guide.md) ·
[Implementation plan](EduEco-Angular-Registration-Profile-Implementation-Plan.md)

---

## 1. Prerequisites

Same one-time setup as the [prior guide](EduEco-Angular-Registration-Profile-Docker-Test-Guide.md#1-prerequisites):
`.env` created from `.env.example`, development certificates generated, and the ASP.NET HTTPS dev certificate trusted.

Free local ports: 4200 (SPA), 7013 (Identity), 7037 (Api), 7100 (BFF), 8025 (Mailpit), 14333 (SQL Server).

## 2. Start the stack

From the repository root:

```bash
docker compose up -d --build
docker compose ps
```

Expected: `eduEco-sqlserver`, `eduEco-redis`, `eduEco-mailpit`, `eduEco-identity`, `eduEco-api`, `eduEco-bff` and
`eduEco-web` running; `eduEco-db-migrator` exited 0. Confirm the invite-email migration applied:

```bash
docker compose logs db-migrator | grep V0010
```

Health checks (each prints `Healthy`):

```bash
curl.exe -sk https://localhost:7013/health/live
curl.exe -sk https://localhost:7037/health/live
```

| URL | What it is |
|---|---|
| http://localhost:4200 | the Angular app (served by the `eduEco-web` container) |
| http://localhost:8025 | Mailpit — every email in this guide lands here |
| https://localhost:7037/scalar | API reference |

> The SPA calls the API cross-origin. Both `http://localhost:4200` and `http://127.0.0.1:4200` are allow-listed
> (`Authentication__AllowedSpaOrigins__*` on the `api` service); any other spelling of the origin will fail CORS.

Sign-in passwords for the seeded accounts are the `DEV_USER_PASSWORD` value in your `.env`.

## 3. Register a school as the platform administrator

1. Open http://localhost:4200 — it redirects to `/login`.
2. Sign in as `platform.admin@eduEco.local` with `DEV_USER_PASSWORD`.
3. **Expected:** you land on `/admin/tenants` ("Register a school"), not `/profile`. The page lists the schools that
   already exist (`Demo School` on a fresh stack).
4. Fill the form:
   - **School code** — lowercase, unique, e.g. `riverside-academy`
   - **School name** — e.g. `Riverside Academy`
   - **Administrator email** — an address you will check in Mailpit, e.g. `head@riverside.local`
   - **Administrator name** — e.g. `Riverside Head`
5. Submit. **Expected:** `Riverside Academy created. An invitation was sent to head@riverside.local.`, and the school
   appears in the list below. **No password is ever shown** — that is the point of the design.

## 4. Set the administrator's password from the email

1. Open http://localhost:8025 and find the message to the administrator address, subject
   *"You are the administrator for Riverside Academy on EduEco"*.
2. The body contains a **set-password link first**, then a sign-in URL. Open the set-password link.
3. Choose a password (12+ characters) and confirm it. **Expected:** *"Your password has been reset."*

The account is created with a random password nobody is told, so this link is the only way in — which is what makes
"the administrator must set a password before first use" true without a forced-change screen.

## 5. Sign in as the new school administrator

1. Back at http://localhost:4200/login, sign in with the administrator email and the password you just set.
2. **Expected:** you land on `/admin` and the top bar shows **Riverside Academy** (read from `GET api/v1/tenants/current`).

## 6. Invite a member and register with the code

1. On the dashboard, fill **Invite a member**: an invitee email, a role (`Student`), and an expiry date.
2. Submit. **Expected:** *"Invite emailed to …"* plus the invite code shown once on screen.
3. In Mailpit, open the message to the invitee, subject *"You've been invited to EduEco"*. **Expected:** the body
   contains the same code shown on screen.
4. Sign out, then choose **Create an account** on the login page.
5. Fill the invite code, the invitee's email, a display name and a password (12+ characters). Submit.
   **Expected:** *"Check your email to confirm your address, then sign in."*
6. In Mailpit, open the confirmation email and follow its link. **Expected:** *"Your email address is confirmed."*
7. Sign in as the new member. **Expected:** you land on `/profile` — not an admin page.

## 7. Negative checks worth doing

| Check | How | Expected |
|---|---|---|
| Role-based landing | Sign in as `student@demo.eduEco.local` or `teacher@demo.eduEco.local` | Lands on `/profile` |
| Admin page is gated | While signed in as a student, browse to http://localhost:4200/admin/tenants | Redirected away, never the provisioning form |
| Duplicate school code | Repeat step 3 with a code that already exists | *"A tenant with code '…' already exists."*, and **no** new email in Mailpit for that attempt |
| Single-use invite | Try registering a second account with the same invite code | Rejected |
| Invalid invite | Register with `not-a-real-code` | *"Invalid invite code."* inline |
| Tenant admin cannot provision | Sign in as `tenant.admin@demo.eduEco.local`, browse to `/admin/tenants` | Redirected away (the API also answers 403) |

## 8. Things that are expected behaviour, not bugs

- **A hard refresh signs you out.** Tokens live in memory only, by design — sign in again.
- **The platform administrator's session cannot be refreshed.** That token is deliberately issued without
  `offline_access`, so when it expires you must sign in again rather than being renewed silently. Ordinary users do
  get a refresh token.
- **The login field is labelled "Email or phone number" but only accepts an email.** Pre-existing validation on the
  login form; phone numbers are stored on the account but are not a sign-in identifier.
- **A platform administrator has no tenant of their own.** `/admin` (the school dashboard) is not for them; their
  page is `/admin/tenants`.

## 9. Automated equivalents

The same chain runs unattended:

```bash
dotnet run --project EduEco/Tools/EduEco.AuthE2E     # 47/47 checks; section K is this guide, sections A–J the rest
dotnet test                                          # 201 tests
```

If `dotnet test` reports containers exiting with code 235, run the test projects one at a time — five suites each
starting their own SQL Server in parallel can exhaust Docker's memory on a developer machine.

## 10. Tear down

```bash
docker compose down        # keep the SQL volume
docker compose down -v     # also drop it (next up re-seeds from scratch)
```
