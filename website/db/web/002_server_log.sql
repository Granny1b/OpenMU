-- =================================================================================================
-- The game server's log, shipped in by Vector so /admin/logs can search it.
--
-- Re-runnable, like every file in this directory.
--
-- WHY THE COLUMNS LOOK LIKE THIS
--
-- Vector's postgres sink builds each row with jsonb_populate_record semantics: the event's JSON
-- keys are matched to columns BY NAME, and a column the event does not carry is inserted as NULL.
-- That is why `id` is a uuid the shipper generates rather than a bigserial - a serial's DEFAULT
-- would never be reached, and the insert would fail on the NOT NULL primary key. Every column here
-- is either set by the pipeline or nullable. See deploy/all-in-one/vector/vector.yaml.
--
-- `id` also gives the log page a total order to page by. Ordering on `at` alone skips and repeats
-- rows whenever two events share a millisecond, which under load they constantly do.
-- =================================================================================================

CREATE TABLE IF NOT EXISTS server_log (
    id        uuid        PRIMARY KEY,
    at        timestamptz NOT NULL,
    level     text        NOT NULL,
    source    text,
    event_id  text,
    message   text        NOT NULL,
    exception text
);

-- The listing order, and the pre-filter every other query leans on.
CREATE INDEX IF NOT EXISTS ix_server_log_at ON server_log (at DESC, id DESC);

-- "show me errors" and "show me everything from the persistence layer" are the two searches this
-- table exists for, so both get an index that ends in the listing order.
CREATE INDEX IF NOT EXISTS ix_server_log_level  ON server_log (level, at DESC);
CREATE INDEX IF NOT EXISTS ix_server_log_source ON server_log (source, at DESC);

-- NOTE ON FREE-TEXT SEARCH. There is deliberately no trigram or tsvector index. pg_trgm needs
-- CREATE EXTENSION, which mu_web_own (the role that runs these migrations) is not superuser enough
-- to do, and a GIN index costs write throughput on the hottest table in the database. A message
-- search is always scoped to a level and a time range in the UI, so it runs over a small slice that
-- the indexes above have already narrowed. If the table ever outgrows that, the fix is a GIN index
-- on to_tsvector('simple', message) - no extension required - not a rethink.

-- ---- privileges ---------------------------------------------------------------------------------
-- mu_web_log is the SHIPPER. It can append and nothing else: it cannot read a line back, cannot
-- amend one, and cannot remove one. A log shipper that could read the table could exfiltrate
-- everything the server ever logged, and one that could delete could cover its own tracks.
GRANT USAGE  ON SCHEMA public TO mu_web_log;
GRANT INSERT ON server_log    TO mu_web_log;

-- The site READS the table, and prunes it. The DELETE is the one thing that separates this table
-- from audit_log: server_log is operational telemetry with a retention policy, so something has to
-- remove old rows or it fills the disk. audit_log keeps its no-DELETE guarantee, and remains the
-- record of who did what - see LogRetentionService for what actually gets deleted here.
GRANT SELECT, DELETE ON server_log TO mu_web_app;

INSERT INTO schema_version (version) VALUES (2) ON CONFLICT DO NOTHING;
