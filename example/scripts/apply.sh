#!/usr/bin/env bash
# Makes the database match sql/schema.sql: shows the plan, asks, then runs it in a transaction. There are no
# migration files; schema.sql is the only description of the schema, and pgschema works out the steps each time.
#
# The database is the one compose.yaml runs; set PGHOST, PGPORT, PGDATABASE, PGUSER or PGPASSWORD to use another.
# Extra arguments go to pgschema, e.g. ./scripts/apply.sh --auto-approve
set -euo pipefail
cd "$(dirname "$0")/.."

docker run --rm -i --network host -v "$PWD:/workspace" \
  -e PGHOST="${PGHOST:-localhost}" -e PGPORT="${PGPORT:-5433}" -e PGDATABASE="${PGDATABASE:-postgres}" \
  -e PGUSER="${PGUSER:-postgres}" -e PGPASSWORD="${PGPASSWORD:-postgres}" \
  pgplex/pgschema:1.14.1 apply --file /workspace/sql/schema.sql "$@"
