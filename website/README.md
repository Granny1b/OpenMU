# Website

A public website for an OpenMU server: registration, rankings, character and guild profiles, and an
admin area gated by the game account's own state.

It is **deliberately not part of the OpenMU solution**. There is no `ProjectReference` into `../src`,
and CI fails the build if one appears. `deploy/all-in-one/docker-compose.yml` pins
`image: munique/openmu`, so the moment website code lives inside the server tree, `docker compose pull`
stops being an upgrade path and you become a fork maintainer.

## How it reaches the data

Directly, over five PostgreSQL roles with **column-level** grants (`db/01b-grants.sql`):

| Role | Reads | Used by |
|---|---|---|
| `mu_web_read` | characters, guilds, classes, account `State`/`IsBot` | every anonymous page |
| `mu_web_auth` | `PasswordHash` | login, change-password, password write |
| `mu_web_reg` | account columns except the hash; writes new accounts and `State` | registration, ban/unban, admin views |
| `mu_web_app` | the site's own `openmu_web` database | sessions, news, audit |
| `mu_web_own` | owns `openmu_web` (DDL only) | the one-shot migrate container |

`SELECT * FROM data."Account"` fails outright as `mu_web_read`. That is an enforced boundary rather
than a code-review convention, and it is why a mistake on a ranking page cannot leak a password hash
or an email address. No role has `DELETE` on an account or a character.

## Setup

```sh
cp .env.example .env && $EDITOR .env         # five DB passwords, MUSITE_ADMINS, MUSITE_OWNER

# OpenMU must have booted at least once - the grants need its schemas to exist.
psql -U postgres -d openmu -v read_pw="'...'" -v auth_pw="'...'" -v reg_pw="'...'" \
                           -v app_pw="'...'"  -v own_pw="'...'" -f db/01-roles.sql
psql -U postgres -d openmu -f db/01b-grants.sql
psql -U postgres -d openmu -f db/02-indexes.sql       # watch for the WARNING it may print
psql -U postgres -d openmu -f db/03-seed-cleanup.sql

docker compose run --rm mu-site-migrate      # creates the openmu_web schema
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

## Tests

```sh
dotnet test
```

`RoleResolverTests` covers every clause of the gating rule; `BCryptCompatibilityTests` pins the hash
format the game must be able to verify. CI additionally runs the schema contract against a throwaway
PostgreSQL, which is the tripwire for upstream schema drift.
