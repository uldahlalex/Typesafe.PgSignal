#!/usr/bin/env bash
# Prints the schema the database has right now, in the same form as sql/schema.sql. Useful to see what someone
# changed by hand: ./scripts/dump.sh > /tmp/live.sql && diff sql/schema.sql /tmp/live.sql
#
# The database is the one compose.yaml runs; set PGHOST, PGPORT, PGDATABASE, PGUSER or PGPASSWORD to use another.
set -euo pipefail
cd "$(dirname "$0")/.."

docker run --rm --network host \
  -e PGHOST="${PGHOST:-localhost}" -e PGPORT="${PGPORT:-5433}" -e PGDATABASE="${PGDATABASE:-postgres}" \
  -e PGUSER="${PGUSER:-postgres}" -e PGPASSWORD="${PGPASSWORD:-postgres}" \
  pgplex/pgschema:1.14.1 dump "$@"
