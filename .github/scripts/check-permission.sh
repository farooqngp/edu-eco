#!/usr/bin/env bash
# Usage: check-permission.sh <owner/repo> <username> <issue-assignee-login>
# Exits 0 (authorized) if username == assignee OR has write/maintain/admin on the repo.
# Requires: gh CLI authenticated as a token with read access to collaborator permissions.
set -euo pipefail

REPO="$1"
USERNAME="$2"
ASSIGNEE="${3:-}"

if [[ -n "$ASSIGNEE" && "$USERNAME" == "$ASSIGNEE" ]]; then
  echo "authorized: $USERNAME is the assignee"
  exit 0
fi

PERMISSION=$(gh api "repos/${REPO}/collaborators/${USERNAME}/permission" --jq .permission 2>/dev/null || echo "none")

case "$PERMISSION" in
  admin|maintain|write)
    echo "authorized: $USERNAME has '$PERMISSION' permission"
    exit 0
    ;;
  *)
    echo "unauthorized: $USERNAME has '$PERMISSION' permission and is not the assignee"
    exit 1
    ;;
esac
