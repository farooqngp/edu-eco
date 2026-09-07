# Phase implementation prompt

You are implementing **Phase {{PHASE_NUMBER}} of {{TOTAL_PHASES}}** of the approved plan for
GitHub issue **#{{ISSUE_NUMBER}}** in `{{REPO}}`, on branch `{{BRANCH_NAME}}` (created from
`dev`).

## What to do

1. Read `CLAUDE.md` at the repo root — follow its coding conventions, build/test/lint
   commands, and commit style exactly. If a convention is undecided there and this phase
   forces a decision, make the decision, state it and your rationale in the PR description,
   and leave `CLAUDE.md` unchanged (don't self-amend project conventions).
2. Read the locked plan comment (the `## 📋 Development Plan` comment on the issue that was
   approved — identifiable as the most recent one before the `/approve` command) and
   implement **only** Phase {{PHASE_NUMBER}}'s described scope. Do not implement later
   phases, even if it seems convenient — small reviewable PRs are the point.
3. Write/update tests for this phase's scope.
4. Run this project's test, lint, and build commands (per `CLAUDE.md`). Do not proceed to
   opening a PR if any of them fail — fix the failure first, or if it's pre-existing and
   unrelated to this phase, note it explicitly in the PR description instead of silently
   ignoring it.
5. Commit with a Conventional Commits message. Push to `{{BRANCH_NAME}}`.
6. Open a pull request (or push additional commits to the existing one if it already
   exists for this phase — check first) with:
   - Base: `dev`, Head: `{{BRANCH_NAME}}`
   - Title: `[#{{ISSUE_NUMBER}}] Phase {{PHASE_NUMBER}}/{{TOTAL_PHASES}}: <phase name>`
   - Body: link `#{{ISSUE_NUMBER}}`, restate the phase's scope from the plan, list what
     changed, note test evidence, and if this is the **final** phase add `Closes
     #{{ISSUE_NUMBER}}`.
   - Add label `sdlc:in-review`.
   - Request review from `@{{ASSIGNEE}}`.
7. Post a short comment on the issue linking the PR.

## Constraints

- Scope discipline is the single most important constraint here: implement exactly one
  phase. If mid-implementation you discover the phase boundary from the plan doesn't make
  sense given the actual code, still implement the originally-scoped phase and note the
  discrepancy in the PR description — do not unilaterally redraw phase boundaries.
- Never push directly to `dev`.
- Use the GitHub token available to you for all `gh`/git remote operations.
