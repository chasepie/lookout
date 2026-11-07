# Deploying Lookout

`docker-compose.yaml` runs Lookout from the images CD (`.github/workflows/cd.yml`) publishes to GHCR. It's generated from the
AppHost's publish mode (`src/AppHost/Production.cs`), so change it there, not here: CI fails while the two differ. Why it's
built the way it is (the networks, the read-only containers, the secrets) is in [`docs/security.md`](../docs/security.md).

The deployment needs only this directory's `docker-compose.yaml` and `.env.example`, not the rest of the repo.

## 1. Settings

Copy `.env.example` to `.env` beside `docker-compose.yaml`, and fill it in. It says what each setting is for, and which are
required. The secrets are there too:

- `LOOKOUT_OWNER_USERNAME` and `LOOKOUT_OWNER_PASSWORD`: what you sign in with. Changing either, other than the username's
  case, signs out every session once the Backend restarts.
- `LOOKOUT_RUNNER_API_KEY`, `BROWSERLESS_TOKEN`, `GLUETUN_API_KEY` and `GLUETUN_PROXY_PASSWORD`: secrets the services share
  among themselves. Make each with `openssl rand -hex 32`.
- `OPENVPN_USER` and `OPENVPN_PASSWORD`: your VPN provider's OpenVPN credentials.

Compose expands `$NAME` in `.env` values, and `#` starts a comment, so put any value with `$`, `#` or spaces in single
quotes: `LOOKOUT_OWNER_PASSWORD='pa$$word with #'`. The `openssl rand -hex` ones never need it.

`LOOKOUT_HOST` is the hostname you reach Lookout on; requests for any other host are refused. `LOOKOUT_TRUSTED_PROXIES` isn't
required, but signing in fails without it: see [step 2](#2-a-reverse-proxy).

Since `.env` holds the secrets, keep it to yourself:

```sh
chmod 600 .env
```

## 2. A reverse proxy

The Backend serves plain HTTP on its published port, `127.0.0.1:8080` unless `LOOKOUT_BIND_ADDRESS` and `LOOKOUT_PORT`
say otherwise, and expects a reverse proxy in front of it. It must:

- **Serve HTTPS**, even if Lookout is only reachable on your LAN or over a VPN: the session cookie is `Secure`, so browsers
  drop it over plain HTTP, and the Backend refuses to sign anyone in over it. Use a certificate your browsers trust, such
  as one from Let's Encrypt with a DNS challenge for a name on a domain you own.
- **Pass the `Host` header through** as `LOOKOUT_HOST`.
- **Send `X-Forwarded-For` and `X-Forwarded-Proto`**, replacing any the client sent. The Backend only trusts them from
  the addresses in `LOOKOUT_TRUSTED_PROXIES`: set it to the proxy's address as the Backend sees it. For a proxy on another
  machine, that's the proxy's LAN address. For a proxy on this machine, connecting to the published port, it's the
  gateway of the Backend's `edge` network: `172.30.240.1`, the first address of `LOOKOUT_EDGE_SUBNET`. The subnet is
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

`LOOKOUT_IMAGE_TAG` picks the images: `main` follows the main branch, a version such as `1.2.0` stays on a release, and
`sha-<commit>` on one commit. To upgrade, change it if needed, then:

```sh
docker compose pull && docker compose up -d
```

Take the new `docker-compose.yaml` and `.env.example` from the same commit or release as the images.

## Backups

Everything Lookout keeps is in Docker volumes:

| Volume | What it holds |
| --- | --- |
| `database` | The SQLite database, and the keys that encrypt session cookies. Losing the keys signs everyone out. |
| `scripts` | The uploaded watch scripts |
| `gluetun` | gluetun's servers list, which it downloads again if lost |

Back up `database` and `scripts` together, with the stack stopped, for example:

```sh
docker compose stop
docker run --rm -v lookout_database:/database:ro -v lookout_scripts:/scripts:ro -v "$PWD":/backup alpine \
  tar czf /backup/lookout-backup.tar.gz /database /scripts
docker compose start
```
