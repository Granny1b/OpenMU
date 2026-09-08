-- Website database roles.
--
-- Run against the `openmu` database as a superuser, AFTER OpenMU has booted at least once
-- (the grants in 01b-grants.sql need the schemas to exist).
--
--   psql -U postgres -d openmu \
--     -v read_pw=... -v auth_pw=... -v reg_pw=... -v app_pw=... -v own_pw=... \
--     -f 01-roles.sql
--
-- Idempotent: safe to run twice. Contains role creation ONLY - every GRANT lives in 01b-grants.sql,
-- because grants live in the database catalog and are destroyed when a `-reinit` drops the database.
--
-- DO NOT add `REVOKE ALL ON DATABASE openmu FROM PUBLIC` here. The four game roles
-- (account, config, guild, friend) are never granted CONNECT anywhere in the OpenMU source -
-- MyNpgsqlMigrationsSqlGenerator.cs:85-113 grants schema USAGE and table DML only. They connect
-- purely on PUBLIC's default. Revoking it stops every player login and character save, while the
-- admin panel keeps working (it connects as postgres), which makes the failure maximally confusing.

\set ON_ERROR_STOP on

-- psql substitutes :'var' only OUTSIDE dollar-quoted strings, so the role creation cannot live in a
-- DO $$ ... $$ block: the :password would reach the server verbatim and fail with a syntax error.
-- Generating the statement with format() and running it through \gexec keeps it both parameterised
-- and idempotent.

SELECT format('CREATE ROLE mu_web_read LOGIN PASSWORD %L', :'read_pw')
 WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'mu_web_read') \gexec

SELECT format('CREATE ROLE mu_web_auth LOGIN PASSWORD %L', :'auth_pw')
 WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'mu_web_auth') \gexec

SELECT format('CREATE ROLE mu_web_reg LOGIN PASSWORD %L', :'reg_pw')
 WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'mu_web_reg') \gexec

SELECT format('CREATE ROLE mu_web_app LOGIN PASSWORD %L', :'app_pw')
 WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'mu_web_app') \gexec

SELECT format('CREATE ROLE mu_web_own LOGIN PASSWORD %L', :'own_pw')
 WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'mu_web_own') \gexec

-- The site's own database. Owned by mu_web_own, which the running site never connects as.
SELECT 'CREATE DATABASE openmu_web OWNER mu_web_own'
 WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'openmu_web') \gexec

GRANT CONNECT ON DATABASE openmu     TO mu_web_read, mu_web_auth, mu_web_reg;
GRANT CONNECT ON DATABASE openmu_web TO mu_web_app, mu_web_own;
