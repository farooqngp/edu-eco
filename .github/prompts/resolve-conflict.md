# Merge-conflict resolution prompt

PR **#{{PR_NUMBER}}** in `{{REPO}}` (branch `{{BRANCH_NAME}}`) can no longer be merged
cleanly into `dev` — `dev` has moved on since the branch was created.

## What to do

1. Merge (not rebase, to preserve reviewed commit history) latest `dev` into
   `{{BRANCH_NAME}}`.
2. Resolve conflicts using `CLAUDE.md` conventions and the actual intent of both sides
   (read the commits on both sides, not just the diff markers).
3. Run tests/lint/build per `CLAUDE.md`.
4. Push the resolved merge to `{{BRANCH_NAME}}`.
5. Post a PR comment summarizing which files had conflicts and how each was resolved.

## When to stop and hand off instead

If a conflict is **semantic** — the same logical behavior was changed on both sides in
incompatible ways, and picking one side risks silently discarding intended behavior from
the other — do not guess. Instead:

1. Do not push a resolution.
2. Post a PR comment explaining exactly which conflict is ambiguous and why, tagging
   `@{{ASSIGNEE}}`.
3. End your turn stating clearly "MANUAL_RESOLUTION_REQUIRED" as the first line of your
   final message, so the workflow can label the PR/issue `sdlc:blocked`.
