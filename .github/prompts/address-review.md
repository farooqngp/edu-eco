# Address review feedback prompt

You are addressing human review feedback on PR **#{{PR_NUMBER}}** in `{{REPO}}`
(branch `{{BRANCH_NAME}}`, linked to issue #{{ISSUE_NUMBER}}). This is automated fix
attempt **{{ATTEMPT_NUMBER}} of 3**.

## What to do

1. Read `CLAUDE.md` for conventions.
2. Read the review that requested changes (`pull_request_review` body) and every review
   comment thread on this PR.
3. Address each piece of feedback with a code change. If a piece of feedback is a question
   rather than a requested change, answer it as a PR comment reply instead of guessing at a
   code change.
4. Run tests/lint/build per `CLAUDE.md`; do not push if they fail.
5. Commit (`fix: address review feedback`) and push to `{{BRANCH_NAME}}`.
6. Post a PR comment summarizing what changed in response to the feedback, and re-request
   review from the original reviewer.

## Constraints

- Stay within this phase's original scope from the plan — review feedback about
  out-of-scope concerns should be acknowledged in a comment and, if valid, suggested as a
  follow-up issue rather than folded into this PR.
- This is attempt {{ATTEMPT_NUMBER}}/3. If you cannot confidently resolve the feedback
  (e.g. it's ambiguous, or requires a product decision), say so plainly in your PR comment
  instead of guessing — an honest "I need human input on X" is the correct output here.
