# gluetun for VPN rotation

VPN Runs go through gluetun's HTTP proxy, set on each Playwright browser context, using PIA over OpenVPN with several regions listed in `SERVER_REGIONS`. Lookout rotates the exit IP every 10–15 minutes, or early after a Known failure, by reconnecting gluetun through its authenticated control API (`PUT /v1/vpn/status` `stopped`, then `running`). This needs no Docker socket and leaves Browserless untouched. Direct Runs share the same Browserless instance without a proxy.

## Considered Options

- **`thrnz/docker-wireguard-pia`.** Rejected even though it was the familiar choice and uses WireGuard. Its regions have only 1–3 servers, so restarting gives little IP variety. Changing region means recreating the container, which needs the Docker socket and can break containers that share its network.
- **Routing Browserless through gluetun's network (`network_mode: service:gluetun`).** Rejected because every Run would go through the VPN, and a gluetun restart would disrupt the browser.
- **gluetun's `PUT /v1/vpn/settings` to switch regions.** Avoided because it is marked experimental. Listing several regions and reconnecting gives region variety through the stable API.

## Consequences

- Swapping PIA for another VPN provider is a gluetun configuration change, not a Lookout code change.
- Lookout talks to the VPN through a small rotator interface, so gluetun itself can also be replaced.
- An HTTP proxy carries only HTTP, and WebRTC's STUN goes around it and reveals the home IP. The runner therefore passes `--webrtc-ip-handling-policy=disable_non_proxied_udp` in the launch options of every Browserless connection, including direct Runs and Test runs. The similarly named `--force-webrtc-ip-handling-policy` does not work.
- gluetun runs its proxy with `HTTPPROXY_STEALTH=on`. Otherwise it forwards the Browserless container's address on plain-HTTP requests.
- The regions are the user's choice, in `SERVER_REGIONS`, and Lookout doesn't restrict them. Keeping them in one country is advised: an exit in another changes what retailers serve (a country picker, another storefront), which would show up as false Changes. The default list is in the US.
- About one server pick in five is a dead server. With its default TCP settings gluetun never leaves one. gluetun therefore runs with `OPENVPN_FLAGS="--connect-retry-max 1 --connect-timeout 10"`, so OpenVPN exits and gluetun picks again by itself. That takes about 35 s, so the rotator doesn't wait for it. Meanwhile `GET /v1/vpn/status` still says `running` (or `crashed`), so a Rotation is one or more attempts, each a `stopped` then `running`:
  - Before the first attempt, the rotator notes the current exit IP and keeps it for the whole Rotation.
  - The Rotation is complete only when the exit IP differs from that noted IP. gluetun picks servers at random without excluding the last one, so the same exit can come back.
  - The exit IP is what `GET /v1/publicip/ip` returns. If gluetun's own lookup fails, an IP fetched through the proxy counts too. Lookout shares no network with the proxy, as pages loaded through it could then reach Lookout, so it fetches that IP the way VPN Runs load pages: through the runner and Browserless. It does so once gluetun has had no exit IP for 10 s, and then at most every 10 s, as a proxy whose tunnel is down holds requests until they time out.
  - If the IP is unchanged, or none arrives within about 15 s, the rotator makes another attempt while VPN Runs stay paused. Attempts don't count against the early-Rotation limit.
  - After 2 minutes without a new exit IP, the Rotation gives up and VPN Runs start again. Otherwise a gluetun that can't reconnect would keep them paused indefinitely. They still fail closed if the VPN isn't connected, but if it reconnected to the noted exit, they go out through it until the next Rotation.
  - At startup there is no noted IP, so any exit IP means the VPN is ready. If none arrives within about 15 s of Lookout starting, the rotator makes an attempt there too. Lookout can't tell when gluetun starts.
  - The control-server role covers `GET`/`PUT /v1/vpn/status`, `GET /v1/publicip/ip` and `PUT /v1/updater/status`, the routes Lookout uses.
- gluetun's bundled PIA server list goes stale. The rotator refreshes it through the control API (`PUT /v1/updater/status`) the first time Lookout sees the VPN connected after Lookout starts, because gluetun's periodic refresh only starts a full period after the container does. Lookout can't tell when gluetun starts, so it skips the refresh if it did one within the last 360 h, keeping when in its database. The `update` command would instead run outside the VPN, from the home IP. After that, gluetun refreshes the list itself every `UPDATER_PERIOD` (at least 360 h, as gluetun's docs ask, so PIA doesn't block the updater) into a persistent `/gluetun` volume.

Sources: [docs/research/script-execution-and-vpn.md](../research/script-execution-and-vpn.md), [docs/research/browser-and-vpn-prototype.md](../research/browser-and-vpn-prototype.md)
