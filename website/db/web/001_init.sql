-- Schema for the website's OWN database (openmu_web). Applied by the one-shot mu-site-migrate
-- container as mu_web_own; the running site connects as mu_web_app and owns nothing here.
--
-- A separate DATABASE, not a schema inside openmu, because ReCreateDatabaseAsync calls
-- EnsureDeletedAsync() = DROP DATABASE openmu. A `-reinit` would otherwise take the sessions,
-- news, ban expiries and audit log with it.

\set ON_ERROR_STOP on

CREATE TABLE IF NOT EXISTS schema_version (
    version     integer     PRIMARY KEY,
    applied_at  timestamptz NOT NULL DEFAULT now()
);

-- Server-side sessions. The cookie carries only the session id, so a ban or password change can
-- revoke a live session; SessionState caches the lookup for 60s and is evicted on every admin action.
CREATE TABLE IF NOT EXISTS web_session (
    id            uuid        PRIMARY KEY,
    account_id    uuid        NOT NULL,
    login_name    text        NOT NULL,
    created_at    timestamptz NOT NULL DEFAULT now(),
    last_seen_at  timestamptz NOT NULL DEFAULT now(),
    expires_at    timestamptz NOT NULL,
    revoked_at    timestamptz,
    ip            inet,
    user_agent    text
);
CREATE INDEX IF NOT EXISTS ix_web_session_account ON web_session (account_id) WHERE revoked_at IS NULL;
CREATE INDEX IF NOT EXISTS ix_web_session_expires ON web_session (expires_at) WHERE revoked_at IS NULL;

-- Ban bookkeeping. data."Account" has NO ban-expiry column, so stock OpenMU's TemporarilyBanned
-- means 'until a human unbans'. prior_state is what unban restores, rather than blindly writing 0
-- and silently demoting a game master.
CREATE TABLE IF NOT EXISTS web_ban (
    id           uuid        PRIMARY KEY,
    account_id   uuid        NOT NULL,
    login_name   text        NOT NULL,
    prior_state  integer     NOT NULL,
    reason       text        NOT NULL DEFAULT '',
    created_at   timestamptz NOT NULL DEFAULT now(),
    expires_at   timestamptz,
    lifted_at    timestamptz,
    actor        text        NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_web_ban_active  ON web_ban (account_id) WHERE lifted_at IS NULL;
CREATE INDEX IF NOT EXISTS ix_web_ban_expiry  ON web_ban (expires_at) WHERE lifted_at IS NULL AND expires_at IS NOT NULL;

CREATE TABLE IF NOT EXISTS news (
    id            uuid        PRIMARY KEY,
    slug          text        NOT NULL UNIQUE,
    title         text        NOT NULL,
    body_markdown text        NOT NULL,
    body_html     text        NOT NULL,
    author        text        NOT NULL,
    is_published  boolean     NOT NULL DEFAULT false,
    is_pinned     boolean     NOT NULL DEFAULT false,
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz NOT NULL DEFAULT now(),
    published_at  timestamptz
);
CREATE INDEX IF NOT EXISTS ix_news_published ON news (is_pinned DESC, published_at DESC) WHERE is_published;

-- Append-only: mu_web_app gets INSERT and SELECT and is NOT the owner, so it cannot grant itself
-- DELETE or TRUNCATE. That non-ownership is the whole guarantee.
CREATE TABLE IF NOT EXISTS audit_log (
    id          bigserial   PRIMARY KEY,
    at          timestamptz NOT NULL DEFAULT now(),
    actor       text        NOT NULL,
    action      text        NOT NULL,
    target_id   uuid,
    target_name text,
    detail      text        NOT NULL DEFAULT '',
    ip          inet
);
CREATE INDEX IF NOT EXISTS ix_audit_at ON audit_log (at DESC);

CREATE TABLE IF NOT EXISTS site_setting (
    key        text        PRIMARY KEY,
    value      text        NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT now()
);

INSERT INTO site_setting (key, value) VALUES
    ('registration_open', 'true'),
    ('maintenance',       'false'),
    ('registrations_today', '0')
ON CONFLICT (key) DO NOTHING;

-- Runtime privileges. mu_web_app can write rows and read them back, and can never remove history.
GRANT USAGE  ON SCHEMA public TO mu_web_app;
GRANT SELECT, INSERT, UPDATE ON web_session, web_ban, news, site_setting TO mu_web_app;
GRANT DELETE ON web_session TO mu_web_app;          -- expired-session cleanup, and nothing else
GRANT SELECT, INSERT ON audit_log TO mu_web_app;    -- deliberately no UPDATE, no DELETE
GRANT USAGE, SELECT ON SEQUENCE audit_log_id_seq TO mu_web_app;
GRANT SELECT ON schema_version TO mu_web_app;

INSERT INTO schema_version (version) VALUES (1) ON CONFLICT DO NOTHING;
