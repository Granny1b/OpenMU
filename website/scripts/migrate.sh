#!/bin/sh
# Applies every db/web/*.sql in filename order, as mu_web_own.
# Each file is idempotent (CREATE TABLE IF NOT EXISTS / ON CONFLICT DO NOTHING), so re-running is safe.
#
# THE MIGRATIONS ARE BAKED INTO THE IMAGE. Dockerfile.migrate does `COPY db/web/ /migrations/`, so
# this script can only apply what was present when the image was built. Adding a .sql file and
# running the container WITHOUT rebuilding it reports "openmu_web is up to date" having skipped the
# new file entirely - which is why the count of files applied is printed rather than just the end
# state. Always `docker compose build mu-site-migrate` first.
set -eu

: "${MUSITE_MIGRATE_CONNSTR:?set MUSITE_MIGRATE_CONNSTR, e.g. postgresql://mu_web_own:pw@database:5432/openmu_web}"

count=0
for file in $(ls /migrations/*.sql | sort); do
    echo "--> applying $(basename "$file")"
    psql "$MUSITE_MIGRATE_CONNSTR" -v ON_ERROR_STOP=1 -f "$file"
    count=$((count + 1))
done

# Says how many, so an image missing a migration is visible here rather than at the first query
# that needs the table. Compare it against `ls website/db/web/*.sql | wc -l` in the checkout.
echo "--> openmu_web is up to date ($count migration(s) in this image)"
