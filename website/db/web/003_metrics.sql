-- Two time series behind /admin/metrics: what the VPS is doing, and what the game server is doing.
--
-- WHY TWO TABLES AND NOT ONE
--
-- They have different writers and therefore different privileges. host_metric is filled by Vector,
-- which runs as mu_web_log and must stay append-only for the same reason the log shipper does. The
-- server samples are taken by the site itself, in-process, so they are written as mu_web_app. One
-- shared table would have to grant the shipper's role a column it has no business touching.

-- ---- the VPS -------------------------------------------------------------------------------------
-- A narrow (name, scope, value) shape rather than a column per metric. Vector's postgres sink maps
-- event fields to columns by name, so a wide table would need a schema change - and a redeploy of
-- the migrate image - every time a metric is added or the host grows a second disk. `scope` is the
-- one tag that separates the series of a metric: the cpu number, the network device, the mount
-- point. It is '' rather than NULL for singular metrics like load1, so that GROUP BY and the unique
-- index below treat "no scope" as a value instead of swallowing rows.
CREATE TABLE IF NOT EXISTS host_metric (
    at    timestamptz      NOT NULL,
    name  text             NOT NULL,
    scope text             NOT NULL DEFAULT '',
    value double precision NOT NULL
);

-- Every dashboard query filters by name and then by time, so that is the leading order. The
-- INCLUDE carries the two columns those queries actually read, which makes every one of them an
-- INDEX ONLY scan - measured at "Heap Fetches: 0" over a million rows. That matters more on a small
-- VPS than anywhere else: without it each query pulls thousands of heap pages that will not be in
-- cache, and the disk, not the processor, is what a cheap server runs out of first.
CREATE INDEX IF NOT EXISTS ix_host_metric_name_at ON host_metric (name, at DESC) INCLUDE (scope, value);

-- Retention deletes by age across every name, and BRIN is exactly right for it: the table is
-- append-only in timestamp order, which is the one case where a block-range index is both accurate
-- and almost free. Measured on a million rows: 24 kB, against roughly 25 MB for the b-tree it
-- replaces, with the same pruning speed - and 25 MB of index that is written on every insert and
-- read once an hour is a poor trade on a machine with 2 GB of RAM.
CREATE INDEX IF NOT EXISTS ix_host_metric_at ON host_metric USING brin (at) WITH (pages_per_range = 32);

-- ---- the game server -----------------------------------------------------------------------------
-- One row per probe. Every measurement except `at` and `is_up` is nullable ON PURPOSE: the site can
-- reach the connect server but fail to complete the protocol handshake, or hold no API key at all,
-- and a sample that records "up, players unknown" is the truth. Writing 0 in those cases would draw
-- a graph that says the server emptied out.
CREATE TABLE IF NOT EXISTS server_sample (
    at           timestamptz NOT NULL,
    is_up        boolean     NOT NULL,
    players      integer,
    load_percent integer,
    servers      integer
);

CREATE INDEX IF NOT EXISTS ix_server_sample_at ON server_sample (at DESC);

-- ---- privileges ----------------------------------------------------------------------------------
-- Same boundary as server_log: the shipper appends and can do nothing else. A metrics shipper that
-- could read this table could work out when the server is empty, which is exactly when a break-in
-- goes unnoticed.
GRANT USAGE  ON SCHEMA public TO mu_web_log;
GRANT INSERT ON host_metric   TO mu_web_log;

-- The site reads both, prunes both, and writes its own samples. It gets no INSERT on host_metric:
-- nothing in the site produces host measurements, so the grant would only widen what a compromised
-- site could forge.
GRANT SELECT, DELETE         ON host_metric   TO mu_web_app;
GRANT SELECT, INSERT, DELETE ON server_sample TO mu_web_app;

INSERT INTO schema_version (version) VALUES (3) ON CONFLICT DO NOTHING;
