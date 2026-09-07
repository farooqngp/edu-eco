# CLAUDE.md

Guidance for any agent (planning or coding) working in this repo. Read this fully before
drafting a plan or writing code. Keep this file current as the codebase grows — the
autonomous SDLC workflows in `.github/workflows/sdlc-*.yml` key their behavior off it.

## Project

`edu-eco` — repo is currently a scaffold with no application code yet. Update this section
with the domain summary, target users, and core architecture once the first features land.

## Conventions (placeholder — fill in as decided)

- **Language / stack**: TBD.
- **Directory layout**: TBD.
- **Build / test / lint commands**: TBD — the autonomous workflows run these after every
  phase's implementation and before opening a PR; keep them accurate.
- **Commit style**: Conventional Commits (`feat:`, `fix:`, `chore:`, etc.).
- **Branch naming**: automation uses `feature/{issue-number}-{slug}` off `dev`.

## For the planning agent (`sdlc-01-plan.yml` / `sdlc-02-plan-command.yml`)

- Read this file, `README.md`, and everything under `docs/**/*.md` before drafting a plan.
- Break work into the smallest set of phases that each produce an independently reviewable
  PR (prefer 100-400 line diffs per phase over one large diff).
- Surface genuine ambiguity as explicit questions in the plan rather than guessing silently.

## For the implementation agent (`sdlc-03-develop-phase.yml`)

- Implement only the current phase's scope from the approved plan — do not scope-creep into
  later phases.
- Follow the conventions above; if a convention is undecided (TBD) and the change forces a
  decision, state the decision and rationale in the PR description.
- Run the project's test/lint commands before pushing; do not open a PR with failing checks.

## Related docs

- `docs/SDLC_AUTOMATION.md` — full design of the autonomous issue→PR→merge pipeline.
