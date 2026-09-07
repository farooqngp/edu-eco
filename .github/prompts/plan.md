# Planning prompt (initial)

You are drafting a development plan for GitHub issue **#{{ISSUE_NUMBER}}**
in `{{REPO}}`, titled "{{ISSUE_TITLE}}", assigned to @{{ASSIGNEE}}.

This is the initial plan (v1) — no prior plan comments exist on this issue.

## What to do

1. Read `CLAUDE.md` at the repo root, `README.md`, and everything under `docs/**/*.md`.
   If `CLAUDE.md` doesn't exist or is a placeholder, note that and proceed using whatever
   context is available (README, existing code structure, issue description).
2. Read the full issue body and all prior comments for context.
3. Understand the codebase structure relevant to this issue (if any code exists yet).
4. Draft a plan using the exact template below. Do not skip the phases section — even a
   single-phase issue must state that explicitly (`Phase 1 — <name>: entire scope`).

## Plan template (post this verbatim, filled in, as your final output — nothing else)

```
<!-- sdlc-plan issue={{ISSUE_NUMBER}} phases=<TOTAL_PHASE_COUNT> -->
## 📋 Development Plan — v{{PLAN_VERSION}}
**Issue:** #{{ISSUE_NUMBER}} — {{ISSUE_TITLE}}
**Assignee:** @{{ASSIGNEE}}

### Understanding
<1-3 sentences: what is actually being asked, in your own words>

### Approach
<the technical approach, key design decisions, why this approach over alternatives>

### Implementation Phases (each phase = one PR into `dev`)
1. **Phase 1 — <name>**: <scope, files/modules touched, why this is a self-contained
   reviewable unit>
2. **Phase 2 — <name>**: <...>
<...continue only as many phases as genuinely needed; prefer fewer, well-scoped phases>

### Test Strategy
<how each phase will be verified — unit tests, manual steps, etc.>

### Risks / Open Questions
<list genuine open questions here, or write "None." — do not invent questions to seem
thorough>

---
**@{{ASSIGNEE}}** — respond with one of:
- `/approve` — lock this plan and start implementation
- `/reject <reason>` — reject and stop (issue can be re-planned later with `/replan`)
- `/revise <feedback or answers to the questions above>` — request changes (up to 3
  revisions allowed)
```

## Constraints

- The `<!-- sdlc-plan ... -->` marker line is machine-parsed. Keep its format exact:
  `phases=<integer>` must match the number of phases you actually listed.
- Do not create branches, commits, or PRs in this job. Planning only posts a comment.
- Post the plan as a single issue comment on #{{ISSUE_NUMBER}} using the GitHub token
  available to you. Do not edit or delete prior plan comments — each version is a new
  comment, preserving history.
