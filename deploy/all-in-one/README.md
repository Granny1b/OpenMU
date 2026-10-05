# All-in-one deployment

The compose files in this folder are documented on the documentation website:

* [All-in-one deployment](../../docs-website/docs/deployment/all-in-one.md) —
  local testing, HTTPS with certbot, and what to do afterwards
* [Deployment overview](../../docs-website/docs/deployment/overview.md) — how
  this variant compares to the others

Short version, for a local test:

```bash
docker compose up -d --no-build
```

The admin panel is then available at http://localhost:8081/ (loopback only). On a
fresh installation it has no user yet and is reachable without a login until one is
created — [create one](../../docs-website/docs/admin-panel/users.md), or set
`OPENMU_ADMIN_USER` and `OPENMU_ADMIN_PASSWORD`, before the server is reachable from
the internet. The production chain (`docker-compose.prod.yml`) requires them.

`munique/openmu` is the published upstream image; to run the server built from the
sources of this repository, add `docker-compose.local-build.yml` to the chain.
