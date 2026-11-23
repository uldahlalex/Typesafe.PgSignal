#!/usr/bin/env bash
# Shows the SQL that apply.sh would run: the difference between sql/schema.sql (what the database should look like)
# and the database itself. Changes nothing.
#
# The database is the one compose.yaml runs; set PGHOST, PGPORT, PGDATABASE, PGUSER or PGPASSWORD to use another.
# Extra arguments go to pgschema, e.g. ./scripts/plan.sh --output-sql stdout
set -euo pipefail
cd "$(dirname "$0")/.."

docker run --rm --network host -v "$PWD:/workspace" \
  -e PGHOST="${PGHOST:-localhost}" -e PGPORT="${PGPORT:-5433}" -e PGDATABASE="${PGDATABASE:-postgres}" \
  -e PGUSER="${PGUSER:-postgres}" -e PGPASSWORD="${PGPASSWORD:-postgres}" \
  pgplex/pgschema:1.14.1 plan --file /workspace/sql/schema.sql "$@"
