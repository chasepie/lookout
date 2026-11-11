# Deploying Lookout

`docker-compose.yaml` runs Lookout from the images each release (`.github/workflows/release.yml`) publishes to GHCR.
It's generated from the AppHost's publish mode (`src/AppHost/Production.cs`), so change it there, not here: CI fails
while the two differ. Why it's built the way it is (the networks, the read-only containers, the secrets) is in
[`docs/security.md`](../docs/security.md).

The deployment needs only this directory's `docker-compose.yaml` and `.env.example`, not the rest of the repo. Take them
from the release you run: each is tagged, such as `v0.4.3`, so they're at
`https://github.com/chasepie/lookout/tree/v0.4.3/deploy`.

## 1. Settings

Copy `.env.example` to `.env` beside `docker-compose.yaml`, and fill it in. It says what each setting is for, and which
are required. The secrets are there too:

- `LOOKOUT_IMAGE_TAG`: the release to run, such as `0.4`. See [Upgrading](#upgrading).
- `LOOKOUT_OWNER_USERNAME` and `LOOKOUT_OWNER_PASSWORD`: what you sign in with. Changing either, other than the
  username's case, signs out every session once the Backend restarts.
- `LOOKOUT_RUNNER_API_KEY`, `BROWSERLESS_TOKEN`, `GLUETUN_API_KEY` and `GLUETUN_PROXY_PASSWORD`: secrets the services
  share among themselves. Make each with `openssl rand -hex 32`.
- `GLUETUN_OPENVPN_USER` and `GLUETUN_OPENVPN_PASSWORD`: your VPN provider's OpenVPN credentials.

Compose expands `$NAME` in `.env` values, and `#` starts a comment, so put any value with `$`, `#` or spaces in single
quotes: `LOOKOUT_OWNER_PASSWORD='pa$$word with #'`. The `openssl rand -hex` ones never need it.

`LOOKOUT_HOST` is the hostname you reach Lookout on; requests for any other host are refused. `LOOKOUT_TRUSTED_PROXIES`
isn't required, but signing in fails without it: see [step 2](#2-a-reverse-proxy).

Since `.env` holds the secrets, keep it to yourself:

```sh
chmod 600 .env
```

## 2. A reverse proxy

The Backend serves plain HTTP on its published port, `127.0.0.1:8080` unless `LOOKOUT_BIND_ADDRESS` and `LOOKOUT_PORT`
say otherwise, and expects a reverse proxy in front of it. It must:

- **Serve HTTPS**, even if Lookout is only reachable on your LAN or over a VPN: the session cookie is `Secure`, so
  browsers drop it over plain HTTP, and the Backend refuses to sign anyone in over it. Use a certificate your browsers
  trust, such as one from Let's Encrypt with a DNS challenge for a name on a domain you own.
- **Pass the `Host` header through** as `LOOKOUT_HOST`.
- **Send `X-Forwarded-For` and `X-Forwarded-Proto`**, replacing any the client sent. The Backend only trusts them from
  the addresses in `LOOKOUT_TRUSTED_PROXIES`: set it to the proxy's address as the Backend sees it. For a proxy on
  another machine, that's the proxy's LAN address. For a proxy on this machine, connecting to the published port, it's
  the gateway of the Backend's `edge` network: `172.30.240.1`, the first address of `LOOKOUT_EDGE_SUBNET`. The subnet is
  fixed so the gateway doesn't change when the network is re-created; change it if it overlaps another network. Trusting
  the gateway trusts every process on this machine that connects to the published port, not just the proxy.
- **Pass WebSocket upgrades** for `/api/hubs/updates`, which carries live updates. Many proxies need this turned on.

If the proxy is on another machine, set `LOOKOUT_BIND_ADDRESS` to this machine's LAN address, and firewall the port so
only the proxy can reach it. Anything else that reaches it can't sign in, since that's plain HTTP, but it shouldn't
reach it at all.

## 3. Start it

```sh
docker compose up -d
docker compose logs -f backend
```

The Backend creates its database on first start. Sign in at `https://<LOOKOUT_HOST>/`.

## Upgrading

`LOOKOUT_IMAGE_TAG` picks the release to run, and is required. Each release, such as `0.4.3`, publishes the images
under its version and also under:

| Tag | Follows |
| --- | --- |
| `0.4` | Before 1.0, each release that keeps the same `docker-compose.yaml` and settings: features and fixes |
| `1` | From 1.0, the same, within a major version |
| `latest` | Every release, including the ones that need a new `docker-compose.yaml` |

Follow `0.4` (or, from 1.0, `1`) to take updates as they're released, or set a version to stay on it. A release that
needs a new `docker-compose.yaml` or a new setting is always a new minor version before 1.0 (`0.5.0`), and a new major
version after (`2.0.0`), so the tag you follow never moves to one. To move to it, take its `docker-compose.yaml` and
`.env.example` from its tag, as in [Deploying Lookout](#deploying-lookout), fill in any new settings in `.env`,
change `LOOKOUT_IMAGE_TAG`, and then:

```sh
docker compose pull && docker compose up -d
```

Any release can migrate the database when the Backend starts, including the ones a tag you follow moves to, and going
back to an earlier release's images doesn't undo that, so keep [backups](#backups).

## Backups

Everything Lookout keeps is in Docker volumes:

| Volume | What it holds |
| --- | --- |
| `database` | The SQLite database, and the keys that encrypt session cookies. Losing the keys signs everyone out. |
| `scripts` | The uploaded watch scripts |
| `gluetun` | gluetun's servers list. If it's lost, gluetun falls back to the list built into its image, which may be months out of date: refresh it as below. |

Back up `database` and `scripts` together, with the stack stopped, for example:

```sh
docker compose stop
docker run --rm -v lookout_database:/database:ro -v lookout_scripts:/scripts:ro -v "$PWD":/backup alpine \
  tar czf /backup/lookout-backup.tar.gz /database /scripts
docker compose start
```

gluetun refreshes its servers list only after `UPDATER_PERIOD` (15 days) without a restart. If the VPN can't connect,
with `TLS key negotiation failed` in gluetun's log, the list is probably out of date. Refresh it, then restart gluetun:

```sh
docker compose stop gluetun
docker run --rm -v lookout_gluetun:/gluetun qmcgaw/gluetun:<the tag in docker-compose.yaml> \
  update -enduser -providers "private internet access"
docker compose start gluetun
```

If you set `GLUETUN_VPN_SERVICE_PROVIDER`, pass that provider to `-providers` instead.
