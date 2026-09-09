# Website

A public website for an OpenMU server: registration, rankings, character and guild profiles, and an
admin area gated by the game account's own state.

It is **deliberately not part of the OpenMU solution**. There is no `ProjectReference` into `../src`,
and CI fails the build if one appears. `deploy/all-in-one/docker-compose.yml` pins
`image: munique/openmu`, so the moment website code lives inside the server tree, `docker compose pull`
stops being an upgrade path and you become a fork maintainer.

## How it reaches the data

Directly, over six PostgreSQL roles with **column-level** grants (`db/01b-grants.sql`):

| Role | Reads | Used by |
|---|---|---|
| `mu_web_read` | characters, guilds, classes, account `State`/`IsBot` | every anonymous page |
| `mu_web_auth` | `PasswordHash` | login, change-password, password write |
| `mu_web_reg` | account columns except the hash; writes new accounts and `State` | registration, ban/unban, admin views |
| `mu_web_app` | the site's own `openmu_web` database | sessions, news, audit |
| `mu_web_own` | owns `openmu_web` (DDL only) | the one-shot migrate container |
| `mu_web_log` | nothing &mdash; `INSERT` on `server_log` only | the Vector log shipper |

`SELECT * FROM data."Account"` fails outright as `mu_web_read`. That is an enforced boundary rather
than a code-review convention, and it is why a mistake on a ranking page cannot leak a password hash
or an email address. No role has `DELETE` on an account or a character.

## Setup

```sh
cp .env.example .env && $EDITOR .env         # six DB passwords, MUSITE_ADMINS, MUSITE_OWNER

# OpenMU must have booted at least once - the grants need its schemas to exist.
# Pass each password RAW - no surrounding quotes. 01-roles.sql uses :'read_pw' inside format(%L),
# which quotes the value itself; -v read_pw="'secret'" produces PASSWORD '''secret''' and the
# quotes end up IN the password, so the role never matches what .env says. All six must be passed:
# psql errors on a variable it was not given, even for a role that already exists.
psql -U postgres -d openmu -f db/01-roles.sql \
  -v read_pw="$READ" -v auth_pw="$AUTH" -v reg_pw="$REG" \
  -v app_pw="$APP"   -v own_pw="$OWN"   -v log_pw="$LOG"
psql -U postgres -d openmu -f db/01b-grants.sql
psql -U postgres -d openmu -f db/02-indexes.sql       # watch for the WARNING it may print
psql -U postgres -d openmu -f db/03-seed-cleanup.sql

# --build is NOT optional. Dockerfile.migrate does `COPY db/web/ /migrations/`, so the SQL is
# BAKED INTO THE IMAGE at build time - an image built before a migration existed will report
# "openmu_web is up to date" while quietly skipping it. Same for mu-site, which is built from
# this source tree: without --build it keeps running the code from the last build.
docker compose build mu-site-migrate mu-site
docker compose run --rm mu-site-migrate      # creates the openmu_web schema, incl. server_log
```

Then verify the boundaries actually hold - the commented block at the end of `db/01b-grants.sql`
lists the four checks, and the last one is **log in with the game client**.

## Account names and capitalisation

OpenMU's unique index on `data."Account"."LoginName"` is **case-sensitive**, so `Valdrenn` and
`valdrenn` are two unrelated accounts as far as the game is concerned. The website refuses a name
that differs from an existing one only by capitalisation, but that check and the insert are two
statements — only a unique index closes the race. `db/02-indexes.sql` creates
`ux_account_loginname_lower` for that, and **skips it with a WARNING** if collisions already exist.
If you see that warning, resolve the pairs it names and re-run the file; until then registration is
open to a case-variant race.

Sign-in is case-sensitive for the same reason: it matches `AccountRepository`, so an account that
can sign in here can always sign in in the game.

## After a `-reinit`

`ReCreateDatabaseAsync` calls `EnsureDeletedAsync()`, which is `DROP DATABASE openmu`. That takes
every grant and index with it, and re-seeds the twenty test accounts. Re-run, in order:

```sh
psql -U postgres -d openmu -f db/01b-grants.sql
psql -U postgres -d openmu -f db/02-indexes.sql       # watch for the WARNING it may print
psql -U postgres -d openmu -f db/03-seed-cleanup.sql
```

The site's own database is untouched, which is why it is a separate database rather than a schema
inside `openmu`. `/healthz/schema` on the internal port names exactly what is missing, per role.

## Admin access

An account gets into `/admin` when **both** hold:

1. its `data."Account"."State"` is `GameMaster` (2) or `GameMasterInvisible` (3), set in the OpenMU
   admin panel; and
2. its login name appears in `MUSITE_ADMINS`.

The second condition is not belt-and-braces. In the OpenMU admin panel, `Accounts.razor` and
`EditAccount.razor` carry no `[Authorize(Policy = AdminPolicies.Administrator)]`, so they fall back
to the blanket `[Authorize]` at `_Imports.razor:27` - which any authenticated panel user satisfies,
including the read-only **Viewer** role. Gating on `State` alone would let a Viewer set any account
to `State = 2` and thereby mint website administrators. See `src/MuSite/Auth/RoleResolver.cs`.

An empty `MUSITE_ADMINS` means zero admins and `/admin` 404s for everybody. Fail closed.

### What an administrator can and cannot do

Every admin route is `/admin/accounts/{id:guid}` and every write is keyed to that **id**. Never a
login name: the search matches names case-insensitively while OpenMU's unique index is
case-sensitive, so a guard and a write that resolve a name differently would clear one account and
modify another.

| | |
|---|---|
| Ban / unban | Admin and Owner |
| Reset a password | **Owner only** — it hands somebody's account to whoever is looking at the screen |
| Act on another administrator | **Owner only** |
| Act on the Owner | nobody |

Banning and resetting re-ask the acting administrator for **their own game password**, verified
against the same hash the game checks. The session cookie proves who signed in — possibly a
fortnight ago, on a laptop now sitting unlocked.

The lever for a rogue administrator is removing them from `MUSITE_ADMINS`, which lives in
configuration rather than in a form anyone can post to.

### What a ban actually does

It writes `data."Account"."State"` and ends the account's website sessions immediately. It **cannot
disconnect a player who is already in the game** — `ChatCommandPlugInBase` disconnects before
writing the state, and the website can only do the write, so an in-game ban takes hold at the next
login.

`data."Account"` has no ban-expiry column, so a temporary ban's expiry lives in the site's own
`web_ban` table and is lifted by a background job. **If the site container is down, temporary bans
do not expire.** Unbanning restores the state recorded when the ban was placed, so a banned game
master does not quietly come back as an ordinary player, and the expiry job leaves the account alone
if someone has since made the ban permanent.

## GM console

`/admin/items`, `/admin/monsters` and `/admin/commands` read the game's own configuration and
compose the exact chat command to run in-game.

`/admin/items` asks in the item's own terms rather than in the command's. `/item`'s three option
arguments look alike and are counted three different ways — `ex` is a bit field over the item's own
excellent options, `opt` is an option *level* for every item except the Dinorant (where it is a
three-bit field), and `anc` is an `AncientSetDiscriminator` whose 1 and 2 name two different real
sets, per item. The builder reads all of that out of `config` and shows tick boxes and named sets;
the arithmetic that turns them back into `ex=44` is the page's job, not the GM's.

**The site does not, and cannot, execute them.** Three independent reasons, each verified in the
server source rather than assumed:

1. A signed-in player's account, characters and inventory are loaded into an EF context created in
   `src/GameLogic/Player.cs:98` and disposed at `:1330` — it lives for the whole session. A row
   written behind its back is untracked, and collides with whatever that session saves.
2. `src/LoginServer/LoginServer.cs:15` keeps connected accounts in a plain in-memory `Dictionary`.
   Online state is never persisted, so the site cannot even tell whether a player is online in
   order to refuse the write.
3. There is no API. `src/Web/Shared/Services/ChatCommandController.cs` only *lists* commands, by
   reflecting over assemblies loaded in the admin panel's own process.

So `/item` drops the item next to you and the player picks it up; `/move` warps them. The console's
job is to make sure what you paste works the first time.

### The command catalogue is generated

`src/MuSite/Game/GmCommands.Generated.cs` is produced from the OpenMU source by
`tools/generate-gm-commands.py`, because the website has no `ProjectReference` into `../src` and so
cannot reflect the commands at runtime. Re-run it after upgrading OpenMU:

```bash
python3 website/tools/generate-gm-commands.py
```

It refuses to write a catalogue with fewer than 50 commands, and `GmCommandsTests` asserts the
shapes it must produce — two earlier versions of that generator each silently dropped arguments,
which is worse than not having one.

### Checking a query against the database

`tools/verify-query-mapping.py` asks PostgreSQL what each catalogue query actually returns and
compares it to the record's constructor — by name, in order, **and by type**:

```bash
MUSITE_TEST_DB="Host=localhost;Username=postgres;Password=...;Database=openmu" \
    python3 website/tools/verify-query-mapping.py
```

Dapper matches constructor parameters to columns pairwise and rejects the constructor outright on
any mismatch, at the first row read — never at compile time, and never on an empty result. Three
separate failures shipped that way: `CharacterProfile` had three columns in the wrong order,
`NewsItem`/`AuditEntry`/`BanRecord` declared `DateTimeOffset` against `timestamptz`, and `ItemRow`
declared `int` against `smallint`. Run this before pushing a query change.

An earlier version compared names and order only, and passed the `ItemRow` queries the day they
were written — the bug was in the types. That is why it checks all three.

### Two things about argument syntax

Most commands take `name=value` pairs in any order, and the short names are matched **exactly and
case-sensitively** by `CommandExtensions.ReadNamedArgumentsAsync` — `ancBonuslvl` works,
`ancbonuslvl` is ignored without complaint. Commands whose argument class carries no `[Argument]`
attributes at all (the whole `/set*` and `/get*` family) are **positional only**: their values go in
declaration order with no gaps, because the server assigns them by index.

22 of the commands are `IDisabledByDefault` and answer "unknown command" until they are enabled on
the OpenMU admin panel's Plugins page. The console flags those.

## Server log

`/admin/logs` searches the game server's own log: filter by level, by category, by time window and
by text, with stack traces expanding inline. Nothing about OpenMU changes to make that work.

The level chips carry counts and deliberately ignore the level filter, so switching between them
shows what you would get - a chip list that narrowed to the selected level would have no way back.
The search covers the message **and** the stack trace, because the text an operator pastes in
("permission denied") is usually in the trace rather than the message. Traces expand with
`<details>`, which is native HTML: the CSP forbids script entirely, so nothing on this site can be
made interactive with JavaScript.

There is no full-text index (see `002_server_log.sql` for why), so a search is scoped to the time
window and a narrower window is a faster search.

**How it gets there.** OpenMU logs through Serilog, and the `munique/openmu` image carries only the
Console and File sinks &mdash; `Serilog.Sinks.Grafana.Loki` is referenced by `src/Dapr/Common`, which
builds the *distributed* image, not this one. So there is no way to point OpenMU straight at a
database without rebuilding its image, which would break `docker compose pull`. Instead it keeps
writing `logs/log.txt` exactly as before, and a [Vector](https://vector.dev) container tails those
files and appends to `openmu_web.server_log`.

```
openmu-startup ──writes──▶ openmu-logs volume ──tails──▶ vector ──INSERT──▶ openmu_web.server_log
                                                                                    │
                                                                       /admin/logs ─┘  (reads)
                                                              LogRetentionService ──┘  (prunes)
```

**It reads the volume, not the Docker socket.** Vector's `docker_logs` source needs
`/var/run/docker.sock`, and read access to that socket is effectively root on the host &mdash; a
container that can talk to it can start another one with the host filesystem mounted. A read-only
bind of the log volume needs no privilege at all.

**The `openmu-logs` volume matters on its own.** Before it existed, `logs/` lived only inside the
container, so every `docker compose up -d --build` threw away the entire history &mdash; which is
exactly when you most want to read what happened beforehand.

**What the shipper may do.** `mu_web_log` can `INSERT` into `server_log` and nothing else. It cannot
read a line back, amend one, or remove one: a shipper able to read the table could exfiltrate
everything the server ever logged, and one able to delete could cover its own tracks. The site reads
the table and prunes it; `server_log` is the only table `mu_web_app` may `DELETE` from, because it
is operational telemetry with a retention window. **`audit_log` keeps its no-DELETE guarantee** and
remains the record of who did what.

**Retention** is `MUSITE_LOGRETENTIONDAYS`, 14 by default, pruned hourly in batches of 20 000. Set
it to 0 to keep everything &mdash; on a busy server that eventually fills the disk, and a full disk
stops PostgreSQL accepting writes and takes the game server down with it.

**Debug and Verbose are dropped by the shipper**, not by OpenMU. OpenMU has 243 `LogDebug` call
sites against 62 `LogInformation`, so raising the game server's level to chase a bug would otherwise
start writing several hundred extra call sites into the database. Raise it freely; the pipeline still
only stores Information and above.

**What is NOT logged.** OpenMU emits no line for a successful login, a completed trade, a ban, or a
GM command being run &mdash; only for a command that *throws*. No shipping fixes an event nobody
emits. Adding them means a plugin against one of the 31 plugin points in
`src/GameLogic/PlugIns/` (`IChatMessageReceivedPlugIn` catches every GM command, since commands
arrive as chat), and `PlugInManager` does load external assemblies from a `plugins/` folder &mdash;
though its `Assembly.LoadFile("plugins\\" + name)` call uses a Windows separator on a relative
path, which `LoadFile` rejects, so that route needs testing on Linux before you rely on it.

### Verifying the pipeline

`tools/verify-log-parse.py` checks the shipper's regex against `tests/fixtures/serilog-sample.txt`,
which is **real** Serilog output produced with the `outputTemplate` from
`src/Startup/appsettings.json` verbatim. It reads the pattern out of `vector/vector.yaml` rather
than keeping a copy, so what is tested is what ships.

That matters because this failure is silent: a pattern that does not match does not error, it files
everything under level `Unparsed`, and a missing log line looks exactly like nothing having
happened. The fixture carries the cases that broke a guessed pattern &mdash; an absent
`SourceContext` renders as empty brackets `[] []` rather than nothing, an `EventId` renders as
`{ Id = 42, Name = ItemCreated }`, and real messages contain brackets
(`picked up by player '[GM] Granny' [slot 3]`), so a loose bracket match eats the message.

```bash
python3 tools/verify-log-parse.py
```

On the server, check Vector's own view before trusting it:

```bash
docker compose exec vector vector validate /etc/vector/vector.yaml   # config is well-formed
docker compose logs --tail=50 vector                                 # parse errors show up here
docker compose exec -T database psql -U postgres -d openmu_web \
  -c "SELECT level, count(*) FROM server_log GROUP BY level ORDER BY 2 DESC"
```

A row count of zero with Vector running usually means the log volume is empty because OpenMU has
not written since the volume was added; a pile of `Unparsed` rows means the regex needs the
attention above.

## Server health

`/admin/metrics` charts the machine and the game side by side, so "the server filled up at eight"
can be read against "the box ran out of memory at eight" on one screen. Two collectors fill it,
both on a 30 second interval so the two sets of series land on the same buckets:

| Source | Writes | What it gives |
| --- | --- | --- |
| Vector's `host_metrics` | `host_metric` as `mu_web_log` | processor, memory, disk, load, network |
| The site's own `ServerProbe` | `server_sample` as `mu_web_app` | up/down, load percentage, players |

### Why the VPS figures need nothing mounted

`/proc` inside a container is the host's `/proc`, so processor, memory and load are the real
machine's without any bind mount. Disk is the interesting one: a Docker volume lives on the host
filesystem, so `statvfs` on `/openmu-logs` reports the **host** disk - the number that actually
fills up. That is why the filesystem collector is restricted to real block-device filesystems and
keyed by device rather than mount point; the log volume and Vector's own data directory are two
mounts of one disk, and keying by mount point would draw it as two identical lines.

Nothing here needs `/var/run/docker.sock`, which is the same call the log shipper made: read access
to that socket is effectively root on the host.

### Player counts need an API key, and only for the exact number

OpenMU keeps the number of connected players in memory - there is no table - and the all-in-one
image ships no metrics exporter. Two sources, layered:

* **The connect server**, spoken as a game client would (`ConnectServerClient`). No credential, and
  it proves the port players actually use is answering. But `LoadPercentage` is
  `(byte)(connections * 100f / MaximumPlayers)`, so at OpenMU's default cap of 1000 one percent is
  ten players and a server with nine online reports **zero**. It is a load gauge, not a counter.
* **`/api/status`**, for the exact figure. This needs an API key:

  1. In OpenMU's admin panel, open **API keys** and create one with the **Viewer** role. Viewer can
     read server status; it cannot send global messages, which needs Operator.
  2. Put it in `.env` as `MUSITE_STATUSAPIKEY`, then `docker compose up -d mu-site`.

  Be aware that the same endpoint also returns the names of everyone online. The website reads the
  count and drops the list, but the key itself can read both.

With no key the page still charts availability and load; the players card says so rather than
drawing a flat zero line.

### What it costs

Measured, not estimated - all on a four core machine:

| | |
| --- | --- |
| Vector, whole pipeline (logs + metrics) | **49 MB** resident, **0.13%** of one core |
| Rows written | ~20 per scrape, ~58,000 a day |
| 30 days of measurements | 1.04 million rows, **138 MB** including indexes |
| Uncached page load, 7 day window | ~150 ms of database work |

Four things keep that small, and each is a deliberate choice rather than a default:

* **The collectors are curated.** `host_metrics` left alone emits 151 rows per scrape, most of it
  loop devices and one row per process. The collector list, the device filters and the
  `vps_wanted` transform cut it to about 20.
* **The read index covers the queries.** `(name, at DESC) INCLUDE (scope, value)` makes every
  dashboard query an index-only scan - measured at `Heap Fetches: 0` over a million rows. On a
  small VPS the disk, not the processor, is what runs out first.
* **The retention index is BRIN.** The table is append-only in timestamp order, which is the one
  case where a block-range index is both accurate and nearly free: **24 kB** against roughly 25 MB
  for the b-tree it replaces.
* **Series are cached for 25 seconds**, just under the collection interval - so reloading the page,
  or a second administrator opening it, costs nothing. There is no newer data to fetch between two
  scrapes. The *response* is deliberately not cached: doing that on a page behind admin
  authentication risks handing one person's view to another.

`MUSITE_METRICSRETENTIONDAYS` (default 14, twice the longest window the page draws) bounds the
growth; 0 keeps everything, which is unbounded on the same disk that holds the database and the
game.

### If the page says the tables do not exist

Same trap as the log table: migrations are baked into `mu-site-migrate` at build time, so an image
built before `003_metrics.sql` reports success having skipped it.

```bash
docker compose build mu-site-migrate
docker compose run --rm mu-site-migrate
```

### The charts have no JavaScript in them

There is no `script-src` in this site's CSP at all, so no charting library can run here. The charts
are SVG built on the server in `Charts/Chart.cs`; hover readouts are SVG `<title>` elements, which
browsers show natively. Two things follow that are easy to undo by accident:

* Colours come from classes in `mu.css`, never from `style=` attributes - the policy drops those,
  and `MarkupTests` fails the build if one appears, including in the chart code.
* Each chart size has its own `viewBox` width, chosen to be close to the width it renders at. An
  SVG with a `viewBox` scales uniformly to its container, **text included**, so one geometry shared
  between a full-width card and a third-width card renders the small card's labels at about six
  pixels: present, correct and unreadable.

## Tests

```sh
dotnet test
```

`RoleResolverTests` covers every clause of the gating rule; `BCryptCompatibilityTests` pins the hash
format the game must be able to verify. CI additionally runs the schema contract against a throwaway
PostgreSQL, which is the tripwire for upstream schema drift.
