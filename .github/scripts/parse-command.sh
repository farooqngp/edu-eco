#!/usr/bin/env bash
# Usage: parse-command.sh <comment-body-file>
# Deterministic slash-command parser. Writes `command` and `payload` to $GITHUB_OUTPUT.
# Recognized commands: /replan, /approve, /reject <reason>, /revise <feedback>
# Only the FIRST line of the comment is inspected; command must start the line (optional
# leading whitespace). Anything after the command word on subsequent lines is included
# in payload for /reject and /revise.
set -euo pipefail

BODY_FILE="$1"
FIRST_LINE=$(head -n1 "$BODY_FILE" | sed -e 's/^[[:space:]]*//')

COMMAND=""
PAYLOAD=""

if [[ "$FIRST_LINE" =~ ^/replan([[:space:]]|$) ]]; then
  COMMAND="replan"
elif [[ "$FIRST_LINE" =~ ^/approve([[:space:]]|$) ]]; then
  COMMAND="approve"
elif [[ "$FIRST_LINE" =~ ^/reject([[:space:]]|$) ]]; then
  COMMAND="reject"
  PAYLOAD=$(sed -e '1s#^/reject[[:space:]]*##' "$BODY_FILE")
elif [[ "$FIRST_LINE" =~ ^/revise([[:space:]]|$) ]]; then
  COMMAND="revise"
  PAYLOAD=$(sed -e '1s#^/revise[[:space:]]*##' "$BODY_FILE")
else
  COMMAND="none"
fi

PAYLOAD_TRIMMED=$(echo "$PAYLOAD" | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//')

DELIM="SDLC_EOF_${GITHUB_RUN_ID:-0}_${GITHUB_RUN_ATTEMPT:-0}_${RANDOM}${RANDOM}"
{
  echo "command=$COMMAND"
  echo "payload<<$DELIM"
  echo "$PAYLOAD_TRIMMED"
  echo "$DELIM"
} >> "$GITHUB_OUTPUT"
