#!/usr/bin/env bash
# Usage: render-template.sh <template-file> KEY=VALUE [KEY=VALUE ...]
# Replaces every {{KEY}} in the template with VALUE (literal, supports multi-line VALUE).
# Prints the rendered result to stdout.
set -euo pipefail

TEMPLATE_FILE="$1"
shift

CONTENT=$(cat "$TEMPLATE_FILE")

for KV in "$@"; do
  KEY="${KV%%=*}"
  VALUE="${KV#*=}"
  CONTENT="${CONTENT//\{\{$KEY\}\}/$VALUE}"
done

printf '%s\n' "$CONTENT"
