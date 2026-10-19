# gluetun for VPN rotation

VPN Runs go through gluetun's HTTP proxy, set on each Playwright browser context, using PIA over OpenVPN with several US regions listed in `SERVER_REGIONS`. Lookout rotates the exit IP every 10–15 minutes, or early after a Known failure, by reconnecting gluetun through its authenticated control API (`PUT /v1/vpn/status` `stopped`, then `running`). This needs no Docker socket and leaves Browserless untouched. Direct Runs share the same Browserless instance without a proxy.

## Considered Options

- **`thrnz/docker-wireguard-pia`.** Rejected even though it was the familiar choice and uses WireGuard. Its regions have only 1–3 servers, so restarting gives little IP variety. Changing region means recreating the container, which needs the Docker socket and can break containers that share its network.
- **Routing Browserless through gluetun's network (`network_mode: service:gluetun`).** Rejected because every Run would go through the VPN, and a gluetun restart would disrupt the browser.
- **gluetun's `PUT /v1/vpn/settings` to switch regions.** Avoided because it is marked experimental. Listing several regions and reconnecting gives region variety through the stable API.

## Consequences

- Swapping PIA for another VPN provider is a gluetun configuration change, not a Lookout code change.
- Lookout talks to the VPN through a small rotator interface, so gluetun itself can also be replaced.
- An HTTP proxy carries only HTTP, and WebRTC's STUN goes around it and reveals the home IP. The runner therefore passes `--webrtc-ip-handling-policy=disable_non_proxied_udp` in the launch options of every Browserless connection, including direct Runs and Test runs. The similarly named `--force-webrtc-ip-handling-policy` does not work.
- gluetun runs its proxy with `HTTPPROXY_STEALTH=on`. Otherwise it forwards the Browserless container's address on plain-HTTP requests.
- Regions are kept to the US. A non-US exit changes what retailers serve (a country picker, another storefront), which would show up as false Changes.
- gluetun reports `running` even while it is stuck retrying a dead server, and it never moves off a dead server by itself. So a Rotation is one or more attempts, each a `stopped` then `running`:
  - Before the first attempt, the rotator notes the current exit IP and keeps it for the whole Rotation.
  - The Rotation is complete only when the exit IP differs from that noted IP. gluetun picks servers at random without excluding the last one, so the same exit can come back.
  - The exit IP is what `GET /v1/publicip/ip` returns. If gluetun's own lookup fails, an IP fetched through the proxy counts too.
  - If the IP is unchanged, or none arrives within about 15 s, the rotator makes another attempt while VPN Runs stay paused. Attempts don't count against the early-Rotation limit.
  - At startup there is no noted IP, so any exit IP means the VPN is ready. If none arrives within about 15 s of gluetun starting, the rotator makes an attempt there too.
  - The control-server role covers `GET /v1/vpn/status`, `PUT /v1/vpn/status` and `GET /v1/publicip/ip`.
- gluetun's bundled PIA server list goes stale. It is refreshed once at install (`gluetun update`), because the periodic refresh (`UPDATER_PERIOD`) first runs only after a full period. After that it refreshes periodically into a persistent `/gluetun` volume.

Sources: [docs/research/script-execution-and-vpn.md](../research/script-execution-and-vpn.md), [docs/research/browser-and-vpn-prototype.md](../research/browser-and-vpn-prototype.md)
