---
title: Public website
sidebar_position: 5
description: Deploy the player-facing website alongside an all-in-one OpenMU server.
---

# Public website

The [website](https://github.com/MUnique/OpenMU/tree/master/website) is a separate container that
serves registration, rankings, character and guild pages, and an admin area gated by the game
account's own state. It reads the same PostgreSQL database as the server, over its own least
privileged roles.

It is deliberately not part of the OpenMU solution and has no reference to any project under `src`,
so `docker compose pull` keeps working as your upgrade path.

## Before you start

The website's grants need OpenMU's schemas to exist, so **start the server once first** and let it
create the database.

:::danger[Close the database first]
Older revisions of `deploy/all-in-one/docker-compose.yml` published PostgreSQL with
`ports: - "5432"`, which binds a random host port on `0.0.0.0` — with `POSTGRES_PASSWORD: admin`.
That publish is gone now. If you have been running an older revision, check with
`docker compose ps` that nothing still exposes 5432, and rotate the database passwords.
:::

## Create the database roles

Five roles, so that no single code path can read every password hash. Generate five distinct
passwords, for example with `openssl rand -base64 24`, and put them in a `.env` file next to the
compose files:

```bash
MUSITE_DB_READ_PW=…
MUSITE_DB_AUTH_PW=…
MUSITE_DB_REG_PW=…
MUSITE_DB_APP_PW=…
MUSITE_DB_OWN_PW=…
```

Then apply the SQL, from the `website` folder:

```bash
psql -h localhost -p 5433 -U postgres -d openmu \
     -v read_pw=… -v auth_pw=… -v reg_pw=… -v app_pw=… -v own_pw=… \
     -f db/01-roles.sql
psql -h localhost -p 5433 -U postgres -d openmu -f db/01b-grants.sql
psql -h localhost -p 5433 -U postgres -d openmu -f db/02-indexes.sql
psql -h localhost -p 5433 -U postgres -d openmu -f db/03-seed-cleanup.sql
```

Port 5433 is the local development publish from `docker-compose.override.yml`. In production the
database is not published at all, so run these from inside the network instead:

```bash
docker compose exec -T database psql -U postgres -d openmu < ../../website/db/01b-grants.sql
```

`02-indexes.sql` prints a **warning** instead of creating `ux_account_loginname_lower` when two
account names differ only by capitalisation. Resolve those pairs and run it again — until you do,
registration is open to a case-variant race.

`03-seed-cleanup.sql` deletes OpenMU's twenty seeded development accounts. Each ships with its
password equal to its login name, and `testgm`, `testgm2` and `testunlock` ship as Game Master.

Finally create the website's own schema:

```bash
docker compose run --rm mu-site-migrate
```

## Configure it

In the same `.env` file:

```bash
MUSITE_SERVERNAME=Aegis MU
MUSITE_ADMIN_1=youraccount        # a GAME account login name
MUSITE_OWNER=youraccount          # must also be one of the admins
MUSITE_MAXPASSWORD=20             # 20 for Season 6, 10 for 0.75/0.95d
MUSITE_CONNECTHOST=play.example.org:44405
```

An account reaches `/admin` only when **both** hold: its state is Game Master, set on the
[Accounts page](../admin-panel/accounts.md) of the admin panel, **and** its login name is listed
above. An empty list means zero admins and `/admin` returns 404.

:::warning[Set the password length once]
A password longer than the game client's field is truncated by the client before it is sent, so its
stored hash can never verify again. Lowering `MUSITE_MAXPASSWORD` later strands every account
registered in between.
:::

## Option A — for local testing

```bash
docker compose build mu-site
docker compose up -d --no-build
```

The website has no published image, so it is always built locally. `--no-build` on the second
command keeps docker compose from rebuilding OpenMU itself from source.

The website answers on [http://localhost/](http://localhost/) and the admin panel moves to
[http://admin.localhost/](http://admin.localhost/) — current browsers resolve `*.localhost` to the
loopback address without any `/etc/hosts` entry.

## Option B — with HTTPS

The admin panel occupies `/` and cannot be moved to a sub-path, so the two get separate hostnames:
the website takes the apex and `www`, the panel moves to `admin.`.

### Add the DNS record

Point `admin.example.org` at the same address as `example.org`.

### Get one certificate covering all three names

The nginx configuration reads certificates from `/etc/nginx/ssl/live/$DOMAIN_NAME/`, a single path,
so all three names must be on **one** certificate — which means one certbot run with all three `-d`
flags together:

```bash
docker compose -f docker-compose.yml -f docker-compose.prod.yml \
  run --rm certbot certonly --webroot --webroot-path /var/www/certbot/ \
  -d example.org -d www.example.org -d admin.example.org
```

:::warning[One run, not three]
Three separate runs produce three certificates in three directories, and nginx will only ever look
in the first. Adding a name later means re-running the whole command with every `-d` flag, not just
the new one.
:::

### Run it

```bash
docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d
```

### Check the split

```bash
curl -sI -H 'Host: example.org'       https://example.org/       | head -1
curl -sI -H 'Host: admin.example.org' https://admin.example.org/ | head -1
```

The first is the website, the second the admin panel. A request with any other hostname is dropped
by a `default_server` block that returns `444`.

## What it cannot do

* **A ban does not disconnect a player who is already in game.** The website writes the account
  state and ends the account's website sessions; an in-game ban takes hold at the player's next
  login.
* **Temporary bans do not expire while the site is down.** `data."Account"` has no expiry column, so
  the expiry lives in the website's own database and is applied by a background job in this
  container.
* **Rankings lag the live game.** Character attributes are flushed on save, not per tick, and the
  site caches boards for two minutes on top. Every board shows when it was last read.

## After a `-reinit`

Re-initializing drops the whole `openmu` database, taking every grant and index with it. The website
then answers every page with a maintenance notice, and `/healthz/schema` on its internal port names
exactly what is missing. Re-run, in order:

```bash
docker compose exec -T database psql -U postgres -d openmu < ../../website/db/01b-grants.sql
docker compose exec -T database psql -U postgres -d openmu < ../../website/db/02-indexes.sql
docker compose exec -T database psql -U postgres -d openmu < ../../website/db/03-seed-cleanup.sql
```

The website's own database is untouched, which is why it is a separate database rather than a schema
inside `openmu`.

## Other deploy variants

Only the **all-in-one** variant carries the website today. Adding it to the others is not difficult,
but neither has been tried, so it is not shipped as if it had been:

* **all-in-one-traefik** — Traefik sets the forwarded headers itself, so no nginx changes are needed,
  but the service needs a `traefik.http.services.mu-site.loadbalancer.server.port=8080` label. No
  service in this repository sets a load balancer port today, and Traefik cannot pick one for a
  container that exposes two.
* **distributed** — `/` is unclaimed there (only `/zipkin`, `/grafana/`, `/admin`, `/serverInfo` and
  `/gameServer/N` are taken), so no hostname split is needed — one service and one `location` block.
  That variant is documented as currently broken regardless.

## What's next

Set your rates and events in the [admin panel](../admin-panel/overview.md), then write a first
announcement at `/admin/news` — the front page shows the three most recent published posts.
