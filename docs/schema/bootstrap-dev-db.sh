#!/usr/bin/env bash
# Bootstraps a local Postgres instance for IAMS backend development. Idempotent — safe to re-run.
#
#   1. Creates the `iams` role + `iams` database if they don't already exist (matches the
#      connection string in src/IAMS.Api/appsettings.Development.json).
#   2. Applies initial-schema.sql (the full chained EF Core migration script).
#   3. Applies seed-dev-data.sql (dev-only seed users/location).
#
# Requires an admin-capable local Postgres role to create the role/database — on a default
# Homebrew/Postgres.app install this is your OS user, connecting to the `postgres` maintenance
# database with trust/peer auth (same as `psql -d postgres` with no -U).
set -euo pipefail

PGHOST="${PGHOST:-localhost}"
PGPORT="${PGPORT:-5432}"
APP_DB="iams"
APP_USER="iams"
APP_PASSWORD="passme2019!"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "==> Creating role '${APP_USER}' (if missing)"
psql -h "$PGHOST" -p "$PGPORT" -d postgres -v ON_ERROR_STOP=1 <<SQL
DO \$\$
BEGIN
   IF NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = '${APP_USER}') THEN
      CREATE ROLE ${APP_USER} WITH LOGIN PASSWORD '${APP_PASSWORD}';
   END IF;
END
\$\$;
SQL

echo "==> Creating database '${APP_DB}' (if missing)"
if ! psql -h "$PGHOST" -p "$PGPORT" -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname='${APP_DB}'" | grep -q 1; then
  psql -h "$PGHOST" -p "$PGPORT" -d postgres -v ON_ERROR_STOP=1 -c "CREATE DATABASE ${APP_DB} OWNER ${APP_USER};"
else
  echo "    already exists, skipping"
fi

echo "==> Applying initial-schema.sql"
PGPASSWORD="$APP_PASSWORD" psql -h "$PGHOST" -p "$PGPORT" -U "$APP_USER" -d "$APP_DB" \
  -v ON_ERROR_STOP=1 -f "$SCRIPT_DIR/initial-schema.sql"

echo "==> Applying seed-dev-data.sql"
PGPASSWORD="$APP_PASSWORD" psql -h "$PGHOST" -p "$PGPORT" -U "$APP_USER" -d "$APP_DB" \
  -v ON_ERROR_STOP=1 -f "$SCRIPT_DIR/seed-dev-data.sql"

echo "==> Done."
