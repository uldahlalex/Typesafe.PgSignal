#!/usr/bin/env bash
# Generates the C# for the database (server/gen/Db: Models.cs with a type per table, QueriesSql.cs with a method per
# query) from sql/schema.sql and sql/queries.sql, as configured in sqlc.yaml. Run it after changing either file.
#
# sqlc runs in Docker; the C# plugin (sqlc-gen-csharp, a .wasm named in sqlc.yaml) is downloaded once and cached in
# ~/.cache/sqlc. Needs no database: sqlc reads the SQL files.
set -euo pipefail
cd "$(dirname "$0")/.."

mkdir -p ~/.cache/sqlc
docker run --rm -e SQLCCACHE=/cache -v ~/.cache/sqlc:/cache -v "$PWD:/src" -w /src \
  sqlc/sqlc:1.31.1 generate
