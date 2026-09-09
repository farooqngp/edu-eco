# CLAUDE.md

Guidance for any agent (planning or coding) working in this repo. Read this file **and every
file it links to under `docs/conventions/`** before drafting a plan or writing code. Keep
these files current as the codebase grows — the autonomous SDLC workflows in
`.github/workflows/sdlc-*.yml` key their behavior off them.

## Project

`edu-eco` — a polyglot monorepo. Domain/product specifics land here once the first real
features are built; for now this section points to the platform architecture that every
service is built on. Full design rationale: `docs/architecture/PLATFORM_ARCHITECTURE.md`
(see also the individual ADRs under `docs/architecture/`).

## Conventions

Split by topic under `docs/conventions/` — read all of them, they are as binding as this
file:

- **[`docs/conventions/stack.md`](docs/conventions/stack.md)** — language/stack per part of
  the repo (.NET 10, Python 3.13, Angular, PostgreSQL, Elasticsearch, Redis, RabbitMQ, YARP).
- **[`docs/conventions/directory-layout.md`](docs/conventions/directory-layout.md)** — the
  full repo directory tree, and the "Two kinds of Contracts" distinction
  (`{Service}.Contracts` vs. `EduEco.Contracts`).
- **[`docs/conventions/backend-services.md`](docs/conventions/backend-services.md)** — Clean
  Architecture layout for every `src/backend/Services/*` microservice (5 projects, one-way
  dependencies), error-handling convention, "never share across services."
- **[`docs/conventions/data-access.md`](docs/conventions/data-access.md)** — **Dapper +
  Dapper Contrib only, no EF Core anywhere in this repo.** Reads vs. writes, unit-of-work
  without change-tracking, domain-event dispatch, DbUp migrations, table/column
  documentation (mandatory `COMMENT ON TABLE`/`COMMENT ON COLUMN` on every migration),
  audit columns + soft delete (baked into `Entity<TId>`, never opt-in), audit-trail
  logging, and the hand-rolled transactional outbox (MassTransit has no Dapper-native
  outbox support).
- **[`docs/conventions/building-blocks.md`](docs/conventions/building-blocks.md)** — the 7
  focused `edueco/BuildingBlocks/*` shared-kernel projects and what each one owns.
- **[`docs/conventions/testing.md`](docs/conventions/testing.md)** — build/test/lint
  commands per stack (scoped by what a phase touched), the 95% coverage gate on
  Domain/Application layers, and the table-documentation enforcement test.
- **[`docs/conventions/local-dev.md`](docs/conventions/local-dev.md)** — local dev
  environment (`docker compose up -d`), Redis key convention, Central Package Management,
  commit style, branch naming.

## For the planning agent (`sdlc-01-plan.yml` / `sdlc-02-plan-command.yml`)

- Read this file, every file under `docs/conventions/`, `README.md`, and everything else
  under `docs/**/*.md` before drafting a plan.
- Break work into the smallest set of phases that each produce an independently reviewable
  PR (prefer 100-400 line diffs per phase over one large diff). For a new microservice,
  typical phasing is: (1) Domain + its unit tests, (2) Application + its unit tests, (3)
  Infrastructure + Contracts (+ integration tests, including the table-documentation
  enforcement test), (4) Api — tests for a layer land in the same phase as that layer, not
  deferred to a final "tests" phase, since Domain/Application each carry their own 95%
  coverage gate (see `docs/conventions/testing.md`) that must pass before that phase's PR
  merges. Combine into fewer phases if the service is small enough to stay within the
  diff-size guidance.
- Surface genuine ambiguity as explicit questions in the plan rather than guessing silently.

## For the implementation agent (`sdlc-03-develop-phase.yml`)

- Implement only the current phase's scope from the approved plan — do not scope-creep into
  later phases.
- Follow the conventions linked above, especially the Clean Architecture layer boundaries,
  the "never share across services" rule, and the Dapper-only data-access rules in
  `docs/conventions/data-access.md` (no EF Core, audit columns + soft delete on every
  table, a `COMMENT` on every table/column) — these are the most common places this kind of
  codebase silently degrades if not enforced per change.
- Run the build/test/lint commands in `docs/conventions/testing.md` (scoped to what this
  phase touched), including the Architecture fitness tests for any backend change, before
  pushing; do not open a PR with failing checks.
- If the phase touched a `Domain` or `Application` project (service or BuildingBlocks), also
  run the coverage-gated command in `docs/conventions/testing.md` and do not open a PR
  below the 95% threshold — write more unit tests, don't lower the bar.
- If the phase creates or alters a table, verify the migration script includes
  `COMMENT ON TABLE`/`COMMENT ON COLUMN` for every table/column, plus the matching
  `v_{table}` soft-delete view and audit columns — per `docs/conventions/data-access.md`.

## Related docs

- `docs/SDLC_AUTOMATION.md` — full design of the autonomous issue→PR→merge pipeline.
- `docs/architecture/` — `PLATFORM_ARCHITECTURE.md` and ADRs for the platform architecture
  decisions summarized in `docs/conventions/`.
