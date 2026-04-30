# pgTAP Test Suite

Schema, function, trigger, and RLS-policy tests for the Heimdall-TicketTracker Postgres database, written against [pgTAP](https://github.com/theory/pgtap/).

This suite is **scaffolding** — it currently contains only a sanity check. New schema-level tests should be added here as the data layer in `src/Heimdall.DAL/` evolves.

## What is pgTAP?

pgTAP is a unit-testing framework for PostgreSQL written in PL/pgSQL and PL/SQL. It produces [TAP](https://testanything.org/) output and is driven by `pg_prove`, a Perl-based test harness. See https://github.com/theory/pgtap/ for full documentation.

## Layout

```
tests/pgtap/
├── README.md                     # this file
├── 00_sanity.sql                 # baseline pgTAP sanity test
├── docker-compose.pgtap.yml      # local Postgres + pgtap container
└── run-tests.sh                  # portable bash runner (uses pg_prove)
```

### Naming convention

Test files use a numeric prefix to enforce execution order:

```
NN_description.sql
```

- `NN` — two-digit (or larger) zero-padded ordinal, e.g. `00`, `10`, `20`.
- `description` — short snake_case summary, e.g. `tickets_schema`, `rls_tenant_isolation`.

Example: `10_tickets_schema.sql`, `20_rls_tenant_isolation.sql`.

## Running the suite

### Option 1 — Docker (recommended)

The bundled compose file uses the [`theory/pgtap`](https://hub.docker.com/r/theory/pgtap) image, which has the pgtap extension preinstalled. The container is mapped to host port **55432** to avoid clashing with the primary `docker-compose.yml` at the repo root.

```bash
docker compose -f tests/pgtap/docker-compose.pgtap.yml up -d
PGPORT=55432 ./tests/pgtap/run-tests.sh
docker compose -f tests/pgtap/docker-compose.pgtap.yml down
```

> **Note on image choice:** `theory/pgtap:latest` is used because it ships with pgtap baked in. If you prefer the official `postgres:17` image, you must install pgtap separately (e.g. via an init script or `apt install postgresql-17-pgtap`) — `run-tests.sh` will still issue `CREATE EXTENSION IF NOT EXISTS pgtap;` but the underlying extension files must be present on disk.

### Option 2 — Existing Postgres + `pg_prove`

If you already have a Postgres instance running and `pg_prove` installed:

```bash
export PGHOST=localhost PGPORT=5432 PGUSER=postgres PGPASSWORD=postgres PGDATABASE=heimdall_test
./tests/pgtap/run-tests.sh
```

The runner will:
1. Verify `psql` and `pg_prove` are on the PATH.
2. Issue `CREATE EXTENSION IF NOT EXISTS pgtap;` against the target database.
3. Invoke `pg_prove` against every `*.sql` file in this directory.

If `pg_prove` is missing, the runner prints install instructions and exits non-zero. The most common installs are:

```bash
# Debian / Ubuntu
sudo apt install libtap-parser-sourcehandler-pgtap-perl postgresql-client

# CPAN
cpanm TAP::Parser::SourceHandler::pgTAP

# macOS
brew install pgtap
```

## Adding a new test

Each file should be self-contained, wrapped in a transaction, and rolled back so it leaves the database untouched. Use this template:

```sql
-- NN_description.sql
BEGIN;

SELECT plan(<number_of_assertions>);

-- Example assertions:
SELECT has_table('public', 'tickets', 'tickets table exists');
SELECT col_not_null('public', 'tickets', 'id');
SELECT col_is_pk('public', 'tickets', 'id');

SELECT * FROM finish();

ROLLBACK;
```

Refer to the [pgTAP function reference](https://pgtap.org/documentation.html) for the full assertion catalog (`has_table`, `has_column`, `col_type_is`, `has_index`, `policies_are`, `function_returns`, …).

## Environment variables

| Variable     | Default          | Notes                                |
| ------------ | ---------------- | ------------------------------------ |
| `PGHOST`     | `localhost`      | Standard libpq variable              |
| `PGPORT`     | `5432`           | Use `55432` with the bundled compose |
| `PGUSER`     | `postgres`       |                                      |
| `PGPASSWORD` | `postgres`       |                                      |
| `PGDATABASE` | `heimdall_test`  |                                      |
