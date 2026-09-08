#!/bin/sh
# Applies every db/web/*.sql in filename order, as mu_web_own.
# Each file is idempotent (CREATE TABLE IF NOT EXISTS / ON CONFLICT DO NOTHING), so re-running is safe.
set -eu

: "${MUSITE_MIGRATE_CONNSTR:?set MUSITE_MIGRATE_CONNSTR, e.g. postgresql://mu_web_own:pw@database:5432/openmu_web}"

for file in $(ls /migrations/*.sql | sort); do
    echo "--> applying $(basename "$file")"
    psql "$MUSITE_MIGRATE_CONNSTR" -v ON_ERROR_STOP=1 -f "$file"
done

echo "--> openmu_web is up to date"
