#!/usr/bin/env bash
# scripts/openfga-bootstrap.sh
#
# One-time (per model edit) OpenFGA bootstrap.
#
# Creates the `heimdall` store if it does not exist, writes the model from
# `authz/model.fga`, and prints the resulting STORE_ID + AUTHORIZATION_MODEL_ID
# so an operator can paste them into Render's web service env vars
# (`OPENFGA_STORE_ID`, `OPENFGA_AUTHORIZATION_MODEL_ID`).
#
# Idempotent: re-running against an existing store does NOT create a duplicate
# store; it writes a NEW model version and emits a new AUTHORIZATION_MODEL_ID
# (model IDs are immutable per OpenFGA — see docs/proposals/openfga.md §3 step 4).
#
# This script does NOT push secrets anywhere. The IDs are printed to stdout
# and written to a JSON file (default: authz/.bootstrap-output.json, gitignored)
# for the operator to copy by hand.
#
# See:
#   - docs/proposals/openfga.md §3 step 4
#   - docs/runbooks/openfga-bootstrap.md
#
# Requirements:
#   - fga CLI    (go install github.com/openfga/cli/cmd/fga@latest)
#   - jq

set -euo pipefail

trap 'echo "ERROR: $(basename "${BASH_SOURCE[0]}") failed at line $LINENO" >&2' ERR

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
MODEL_FILE="${REPO_ROOT}/authz/model.fga"
STORE_NAME="heimdall"
OUTPUT_PATH="${REPO_ROOT}/authz/.bootstrap-output.json"

usage() {
  cat <<EOF
Usage: $(basename "$0") [--output PATH]

  --output PATH   Write {store_id, authorization_model_id} JSON here.
                  Default: authz/.bootstrap-output.json (gitignored)

Required env:
  OPENFGA_API_URL          e.g. http://heimdall-ticket-tracker-openfga:8080
  OPENFGA_PRESHARED_KEY    preshared API token

Required CLI tools: fga, jq
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --output)
      if [[ $# -lt 2 || -z "${2:-}" || "$2" == --* ]]; then
        echo "ERROR: --output requires a path argument" >&2
        usage >&2
        exit 2
      fi
      OUTPUT_PATH="$2"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "ERROR: unknown argument: $1" >&2
      usage >&2
      exit 2
      ;;
  esac
done

# --- Preconditions ----------------------------------------------------------

: "${OPENFGA_API_URL:?ERROR: OPENFGA_API_URL is required (e.g. http://heimdall-ticket-tracker-openfga:8080)}"
: "${OPENFGA_PRESHARED_KEY:?ERROR: OPENFGA_PRESHARED_KEY is required}"

for cmd in fga jq; do
  if ! command -v "$cmd" >/dev/null 2>&1; then
    echo "ERROR: required CLI '$cmd' not found in PATH" >&2
    exit 1
  fi
done

if [[ ! -f "$MODEL_FILE" ]]; then
  echo "ERROR: model file not found at $MODEL_FILE" >&2
  exit 1
fi

# fga CLI reads these env vars for every subcommand.
export FGA_API_URL="$OPENFGA_API_URL"
export FGA_API_TOKEN="$OPENFGA_PRESHARED_KEY"

# --- Find or create store ---------------------------------------------------

echo "Looking up store '${STORE_NAME}' at ${OPENFGA_API_URL}..." >&2

# `fga store list` emits JSON by default; the `--format` flag is not supported
# on this subcommand (verified against fga CLI). Let auth/connectivity
# failures surface (so a misconfigured token doesn't get misread as "no
# stores" and trigger a spurious create); only an empty store list on a
# successful call should fall through to the create path.
if ! STORE_LIST_JSON="$(fga store list 2>&1)"; then
  echo "ERROR: 'fga store list' failed against ${OPENFGA_API_URL}:" >&2
  printf '%s\n' "$STORE_LIST_JSON" >&2
  echo "ERROR: check OPENFGA_API_URL, OPENFGA_PRESHARED_KEY, and network reachability." >&2
  exit 1
fi

STORE_ID="$(
  printf '%s' "$STORE_LIST_JSON" \
    | jq -r --arg name "$STORE_NAME" \
        '(.stores // []) | map(select(.name == $name)) | .[0].id // empty'
)"

if [[ -z "$STORE_ID" ]]; then
  echo "Store '${STORE_NAME}' not found — creating..." >&2
  # `fga store create` emits JSON by default; `--format` here would refer to
  # an *authorization model* input format (only meaningful with `--model FILE`),
  # so we omit it.
  CREATE_JSON="$(fga store create --name "$STORE_NAME")"
  STORE_ID="$(printf '%s' "$CREATE_JSON" | jq -r '.store.id // .id // empty')"
  if [[ -z "$STORE_ID" ]]; then
    echo "ERROR: could not parse store id from 'fga store create' output:" >&2
    printf '%s\n' "$CREATE_JSON" >&2
    exit 1
  fi
  echo "Created store ${STORE_ID}." >&2
else
  echo "Reusing existing store ${STORE_ID}." >&2
fi

# --- Write model ------------------------------------------------------------

echo "Writing model from ${MODEL_FILE}..." >&2

# `fga model write` autodetects the input format from the file extension
# (`.fga` -> DSL). Passing `--format json` would force the CLI to parse the
# DSL file as JSON and fail; let autodetect handle it.
MODEL_JSON="$(fga model write --store-id "$STORE_ID" --file "$MODEL_FILE")"

AUTHORIZATION_MODEL_ID="$(
  printf '%s' "$MODEL_JSON" \
    | jq -r '.authorization_model_id // .model.id // .id // empty'
)"

if [[ -z "$AUTHORIZATION_MODEL_ID" ]]; then
  echo "ERROR: could not parse authorization_model_id from 'fga model write' output:" >&2
  printf '%s\n' "$MODEL_JSON" >&2
  exit 1
fi

# --- Emit results -----------------------------------------------------------

mkdir -p "$(dirname "$OUTPUT_PATH")"
jq -n \
  --arg store_id "$STORE_ID" \
  --arg authorization_model_id "$AUTHORIZATION_MODEL_ID" \
  '{store_id: $store_id, authorization_model_id: $authorization_model_id}' \
  > "$OUTPUT_PATH"

echo "Wrote ${OUTPUT_PATH}" >&2
echo >&2
echo "Paste the following into Render's web service env vars:" >&2
echo >&2

# Parseable stdout (so callers can `eval` or `source` if they want).
printf 'STORE_ID=%s\n' "$STORE_ID"
printf 'AUTHORIZATION_MODEL_ID=%s\n' "$AUTHORIZATION_MODEL_ID"
