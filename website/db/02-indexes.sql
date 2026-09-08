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
-- Case-collision check. data."Account"."LoginName" carries a plain unique btree, which is
-- CASE-SENSITIVE: 'Player' and 'player' can both exist. The site logs in with an exact `=` match,
-- the same as the game (AccountRepository), so the login path is unambiguous - but run this once
-- and record the answer before enabling the optional hardening index below.
-- ---------------------------------------------------------------------------------------------
SELECT lower("LoginName") AS collides, count(*) AS variants
  FROM data."Account"
 GROUP BY lower("LoginName")
HAVING count(*) > 1;

-- OPTIONAL HARDENING - apply only if the query above returned zero rows.
-- It makes case-variant registrations impossible, which closes the whole class of confusion
-- between a case-insensitive lookup and a case-sensitive write. It will FAIL if collisions exist.
--
-- CREATE UNIQUE INDEX CONCURRENTLY ux_account_loginname_lower ON data."Account" (lower("LoginName"));
