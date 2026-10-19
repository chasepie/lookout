# PROTOTYPE: browser and VPN questions (issue #2)

Throwaway code. It lives only on the `prototype/browser-and-vpn` branch and is never merged into main. The findings are written up in [`docs/research/browser-and-vpn-prototype.md`](https://github.com/chasepie/lookout/blob/main/docs/research/browser-and-vpn-prototype.md) on main; the logs and screenshots behind them are in [`results/`](results/).

## Run it

Put PIA credentials in `pia.env` at the repo root (gitignored by `*.env`):

```sh
OPENVPN_USER=…
OPENVPN_PASSWORD=…
```

Then, from this directory:

```sh
docker compose up -d                                   # Browserless v2.57.1 + gluetun v3.41.3
docker compose run --rm --no-deps gluetun update -enduser -providers "private internet access"   # refresh the server list
node prototype.ts bot                                  # Q1: every browser mode, direct
node prototype.ts bot browserless-headless-ua          # Q1: one mode
node prototype.ts load                                 # Q1: time to the load event per mode, and memory
node prototype.ts bot --proxy                          # Q3: every Browserless mode, through gluetun
node prototype.ts fail-closed                          # Q2: stop the VPN and the container, probe the proxy
node prototype.ts webrtc --screenshots                 # Q2: does WebRTC reveal the home IP around the proxy?
node prototype.ts probe                                # exit IP through the proxy over HTTPS, HTTP and an IP literal
node prototype.ts rotate 20                            # Q3: 20 Rotations, timed
docker compose down -v
```

gluetun can start on a dead server and never leave it. If `curl -H 'X-API-Key: prototype-only-not-a-secret' 127.0.0.1:8000/v1/publicip/ip` stays empty, `PUT /v1/vpn/status` `stopped` then `running` to re-roll.

Override the compose defaults with `OPENVPN_PROTOCOL=udp`, `HTTPPROXY_STEALTH=on` or `SERVER_REGIONS=…` in the environment.

## Reading the results

- Every line printed, and every screenshot, replaces the home IP with `<home-ip>`. `"leak": true` on a **direct** context only means "this is the home IP", which is expected. It is a real leak only on a proxied context.
- Direct pages can show the home location (Target's "Ship to <ZIP>"). Direct-mode retailer pages are therefore not screenshotted, and the committed logs had the ZIP, store and time zone replaced with `<home-zip>`, `<home-store>` and `<home-timezone>`.
- `q3-rotation-shared-page.log` is a discarded measurement. It probed from one long-lived page, which queued behind CONNECTs it had abandoned while the tunnel was down, so its times are inflated. `q3-rotation.log` uses a fresh context per probe.
- Screenshots were converted to 800 px wide JPEGs to keep the branch small.
