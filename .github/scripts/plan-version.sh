#!/usr/bin/env bash
# Usage: plan-version.sh <owner/repo> <issue-number>
# Prints, to stdout:
#   version=<n>                total historical "## 📋 Development Plan" comments + 1
#   plans_since_boundary=<n>   plan comments posted since the most recent
#                              "🔁 Replanning" marker comment (0 if none posted yet since
#                              the boundary, or since issue creation if never replanned)
#
# Revision accounting built on top of this (by callers):
#   revisions_used = max(plans_since_boundary - 1, 0)   -- v1 of a cycle isn't a "revision"
# Cap is 3 revisions per cycle => block further /revise once revisions_used >= 3.
set -euo pipefail

REPO="$1"
ISSUE_NUMBER="$2"

mapfile -t ENC_BODIES < <(gh api "repos/$REPO/issues/$ISSUE_NUMBER/comments" --paginate --jq '.[].body | @base64')

BOUNDARY=-1
TOTAL=0
for i in "${!ENC_BODIES[@]}"; do
  BODY=$(printf '%s' "${ENC_BODIES[$i]}" | base64 -d)
  if printf '%s' "$BODY" | grep -q "🔁 Replanning"; then
    BOUNDARY=$i
  fi
  if printf '%s' "$BODY" | grep -q "## 📋 Development Plan"; then
    TOTAL=$((TOTAL + 1))
  fi
done

SINCE_BOUNDARY=0
for i in "${!ENC_BODIES[@]}"; do
  if [ "$i" -le "$BOUNDARY" ]; then
    continue
  fi
  BODY=$(printf '%s' "${ENC_BODIES[$i]}" | base64 -d)
  if printf '%s' "$BODY" | grep -q "## 📋 Development Plan"; then
    SINCE_BOUNDARY=$((SINCE_BOUNDARY + 1))
  fi
done

echo "version=$((TOTAL + 1))"
echo "plans_since_boundary=$SINCE_BOUNDARY"
echo "total_plans=$TOTAL"
