# Planning prompt (revision)

You are revising the development plan for GitHub issue **#{{ISSUE_NUMBER}}** in `{{REPO}}`,
titled "{{ISSUE_TITLE}}", assigned to @{{ASSIGNEE}}.

This is revision request **{{REVISION_NUMBER}} of 3**. The previous plan is the most recent
`## 📋 Development Plan` comment on the issue. The assignee's feedback to incorporate is:

> {{FEEDBACK}}

Read the previous plan comment before writing the new one. Address the feedback directly —
if it answered a question you asked, reflect that answer in the new plan and don't ask it
again. If the feedback conflicts with something already decided, prefer the feedback. Do
not silently drop parts of the previous plan that the feedback didn't touch.

## What to do

1. Read `CLAUDE.md`, `README.md`, and `docs/**/*.md` (same as initial planning).
2. Read the previous plan comment in full.
3. Draft the revised plan using the exact template below.

## Plan template (post this verbatim, filled in, as your final output — nothing else)

```
<!-- sdlc-plan issue={{ISSUE_NUMBER}} phases=<TOTAL_PHASE_COUNT> -->
## 📋 Development Plan — v{{PLAN_VERSION}}
**Issue:** #{{ISSUE_NUMBER}} — {{ISSUE_TITLE}}
**Assignee:** @{{ASSIGNEE}}

### What changed since v{{PREVIOUS_VERSION}}
<1-3 sentences summarizing what this revision changes, directly tied to the feedback above>

### Understanding
<1-3 sentences: what is actually being asked, in your own words>

### Approach
<the technical approach, key design decisions, why this approach over alternatives>

### Implementation Phases (each phase = one PR into `dev`)
1. **Phase 1 — <name>**: <scope, files/modules touched, why this is a self-contained
   reviewable unit>
2. **Phase 2 — <name>**: <...>

### Test Strategy
<how each phase will be verified>

### Risks / Open Questions
<list genuine open questions here, or write "None.">

---
**@{{ASSIGNEE}}** — respond with one of:
- `/approve` — lock this plan and start implementation
- `/reject <reason>` — reject and stop (issue can be re-planned later with `/replan`)
- {{REVISE_HINT}}
```

## Constraints

- The `<!-- sdlc-plan ... -->` marker line is machine-parsed; `phases=<integer>` must match
  the number of phases you listed.
- Do not create branches, commits, or PRs in this job.
- Post as a new issue comment; do not edit or delete the previous plan comment.
