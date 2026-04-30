#!/usr/bin/env bash
# run-tests.sh — pgTAP test runner for Heimdall-TicketTracker.
#
# Reads connection settings from the standard libpq environment variables and
# executes every tests/pgtap/*.sql file via pg_prove. Ensures the pgtap
# extension is installed in the target database before running.
#
# Usage:
#   ./run-tests.sh
#
# Environment variables (with defaults):
#   PGHOST     (localhost)
#   PGPORT     (5432)
#   PGUSER     (postgres)
#   PGPASSWORD (postgres)
#   PGDATABASE (heimdall_test)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

export PGHOST="${PGHOST:-localhost}"
export PGPORT="${PGPORT:-5432}"
export PGUSER="${PGUSER:-postgres}"
export PGPASSWORD="${PGPASSWORD:-postgres}"
export PGDATABASE="${PGDATABASE:-heimdall_test}"

echo "pgTAP runner — host=${PGHOST} port=${PGPORT} db=${PGDATABASE} user=${PGUSER}"

if ! command -v psql >/dev/null 2>&1; then
    echo "ERROR: psql is not installed. Install the PostgreSQL client (e.g. 'apt install postgresql-client')." >&2
    exit 1
fi

if ! command -v pg_prove >/dev/null 2>&1; then
    cat >&2 <<'EOF'
ERROR: pg_prove is not installed. pg_prove ships with the pgTAP TAP::Parser source handler.

Install it with one of:
  # Debian / Ubuntu
  sudo apt install libtap-parser-sourcehandler-pgtap-perl postgresql-client

  # CPAN (any platform with Perl)
  cpanm TAP::Parser::SourceHandler::pgTAP

  # macOS (Homebrew)
  brew install pgtap

See https://github.com/theory/pgtap/ for details.
EOF
    exit 1
fi

# Ensure the pgtap extension is installed in the target database.
echo "Ensuring pgtap extension is present..."
psql --no-psqlrc -v ON_ERROR_STOP=1 -c "CREATE EXTENSION IF NOT EXISTS pgtap;"

echo "Running pgTAP suite..."
pg_prove --ext .sql -r "${SCRIPT_DIR}"
