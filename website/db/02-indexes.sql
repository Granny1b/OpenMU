-- Additive performance indexes on the `openmu` database. Nothing here changes OpenMU's schema:
-- every statement is CREATE INDEX IF NOT EXISTS, and dropping them only makes pages slower.
-- Like the grants, these live in the openmu database and must be RE-RUN after a `-reinit`.
--
--   psql -U postgres -d openmu -f 02-indexes.sql

\set ON_ERROR_STOP on

-- Rankings read one row per (character, stat). Without this, every board is a sequential scan of
-- data."StatAttribute", which holds roughly (characters x stats) rows.
CREATE INDEX IF NOT EXISTS ix_statattribute_char_def_value
    ON data."StatAttribute" ("CharacterId", "DefinitionId") INCLUDE ("Value");

-- The reverse order, for "top N by this one stat" before the character join.
CREATE INDEX IF NOT EXISTS ix_statattribute_def_value
    ON data."StatAttribute" ("DefinitionId", "Value" DESC);

-- Character name search on /rankings and /admin/accounts (prefix ILIKE).
CREATE INDEX IF NOT EXISTS ix_character_name_lower
    ON data."Character" (lower("Name") text_pattern_ops);

-- Account login-name search on /admin/accounts.
CREATE INDEX IF NOT EXISTS ix_account_loginname_lower
    ON data."Account" (lower("LoginName") text_pattern_ops);

-- ---------------------------------------------------------------------------------------------
-- Case-insensitive uniqueness for account names.
--
-- data."Account"."LoginName" carries a plain unique btree, which is CASE-SENSITIVE: 'Valdrenn' and
-- 'valdrenn' can both exist, and to the game they are two unrelated accounts - a ready-made
-- impersonation. The website refuses a name that differs from an existing one only by
-- capitalisation, but that check and the insert are two statements: under concurrent registrations
-- one can still slip through. Only a unique index closes the race, so this creates it.
--
-- It is skipped, loudly, when collisions already exist - resolve those first (rename or delete one
-- of each pair), then re-run this file.
-- ---------------------------------------------------------------------------------------------
DO $$
DECLARE
    collisions integer;
BEGIN
    SELECT count(*) INTO collisions FROM (
        SELECT lower("LoginName") FROM data."Account" GROUP BY 1 HAVING count(*) > 1
    ) AS duplicated;

    IF collisions > 0 THEN
        RAISE WARNING
            'ux_account_loginname_lower NOT created: % account name(s) differ only by capitalisation. '
            'Registration remains open to a case-variant race until they are resolved. '
            'List them with: SELECT lower("LoginName"), count(*) FROM data."Account" '
            'GROUP BY 1 HAVING count(*) > 1;', collisions;
    ELSE
        CREATE UNIQUE INDEX IF NOT EXISTS ux_account_loginname_lower
            ON data."Account" (lower("LoginName"));
        RAISE NOTICE 'ux_account_loginname_lower is in place.';
    END IF;
END
$$;
