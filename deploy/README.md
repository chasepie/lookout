# Deploying Lookout

`docker-compose.yaml` runs Lookout from the images CI publishes to GHCR. It's generated from the AppHost's publish mode
(`src/AppHost/Production.cs`) by `publish.sh`, so change it there, not here: CI fails while the two differ. Why it's built
the way it is (the networks, the read-only containers, the secrets) is in [`docs/security.md`](../docs/security.md).

The deployment needs only this directory's `docker-compose.yaml` and `.env.example`, not the rest of the repo.

## 1. Settings

Copy `.env.example` to `.env` beside `docker-compose.yaml`, and fill it in. It says what each setting is for. These are
required:

- `LOOKOUT_HOST`: the hostname you reach Lookout on. Requests for any other host are refused.
- `LOOKOUT_OWNER_USERNAME`: the username you sign in with.
- `BROWSERLESS_TOKEN` and `GLUETUN_API_KEY`: two secrets, made with `openssl rand -hex 32`. They're here rather than
  in `secrets/` because browserless and gluetun can only read them from their environment or a config file.
- `LOOKOUT_TRUSTED_PROXIES`: not strictly required, but signing in fails without it; see [step 3](#3-a-reverse-proxy).

```sh
chmod 600 .env
```

## 2. Secrets

Each file in `secrets/` holds one secret, with nothing else in it but an optional trailing newline:

| File | What it holds |
| --- | --- |
| `owner_password` | The password you sign in with, at least 16 characters |
| `runner_api_key` | The key the Backend sends the Runner: `openssl rand -hex 32` |
| `vpn_proxy_password` | The password of gluetun's HTTP proxy: `openssl rand -hex 32` |
| `openvpn_user` | Your VPN provider's OpenVPN username |
| `openvpn_password` | Your VPN provider's OpenVPN password |

Compose mounts each file into the containers that need it, at `/run/secrets`, keeping them out of the containers'
environments. The files are mounted as they are, owner and mode included, and the Backend and the Runner run as uid
1654, so give them the first three:

```sh
mkdir -p secrets && chmod 700 secrets
openssl rand -hex 32 > secrets/runner_api_key
openssl rand -hex 32 > secrets/vpn_proxy_password
# ...and write owner_password, openvpn_user and openvpn_password yourself
sudo chown 1654 secrets/owner_password secrets/runner_api_key secrets/vpn_proxy_password
sudo chmod 400 secrets/*
```

Changing the Owner's password, or their username other than its case, signs out every session once the Backend restarts.

## 3. A reverse proxy

The Backend serves plain HTTP on its published port, `127.0.0.1:8080` unless `LOOKOUT_BIND_ADDRESS` and `LOOKOUT_PORT`
say otherwise, and expects a reverse proxy in front of it. It must:

- **Serve HTTPS**, even if Lookout is only reachable on your LAN or over a VPN: the session cookie is `Secure`, so browsers
  drop it over plain HTTP, and the Backend refuses to sign anyone in over it. Use a certificate your browsers trust, such
  as one from Let's Encrypt with a DNS challenge for a name on a domain you own.
- **Pass the `Host` header through** as `LOOKOUT_HOST`.
- **Send `X-Forwarded-For` and `X-Forwarded-Proto`**, replacing any the client sent. The Backend only trusts them from
  the addresses in `LOOKOUT_TRUSTED_PROXIES`: set it to the proxy's address as the Backend sees it. For a proxy on another
  machine, that's the proxy's LAN address. For a proxy on this machine, connecting to the published port, it's usually
  the gateway of the Backend's `edge` network: `docker network inspect <project>_edge --format '{{(index .IPAM.Config 0).Gateway}}'`.
- **Pass WebSocket upgrades** for `/api/hubs/updates`, which carries live updates. Many proxies need this turned on.

If the proxy is on another machine, set `LOOKOUT_BIND_ADDRESS` to this machine's LAN address, and firewall the port so
only the proxy can reach it. Anything else that reaches it can't sign in, since that's plain HTTP, but it shouldn't
reach it at all.

## 4. Start it

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
docker run --rm -v <project>_database:/database:ro -v <project>_scripts:/scripts:ro -v "$PWD":/backup alpine \
  tar czf /backup/lookout-backup.tar.gz /database /scripts
docker compose start
```
