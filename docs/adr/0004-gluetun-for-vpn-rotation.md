# gluetun for VPN rotation

VPN Runs go through gluetun's HTTP proxy, set on each Playwright browser context, using PIA over OpenVPN with several US regions listed in `SERVER_REGIONS`. Lookout rotates the exit IP every 10–15 minutes, or early after a Known failure, by reconnecting gluetun through its authenticated control API (`PUT /v1/vpn/status` `stopped`, then `running`). This needs no Docker socket and leaves Browserless untouched. Direct Runs share the same Browserless instance without a proxy.

## Considered Options

- **`thrnz/docker-wireguard-pia`.** Rejected even though it was the familiar choice and uses WireGuard. Its regions have only 1–3 servers, so restarting gives little IP variety. Changing region means recreating the container, which needs the Docker socket and can break containers that share its network.
- **Routing Browserless through gluetun's network (`network_mode: service:gluetun`).** Rejected because every Run would go through the VPN, and a gluetun restart would disrupt the browser.
- **gluetun's `PUT /v1/vpn/settings` to switch regions.** Avoided because it is marked experimental. Listing several regions and reconnecting gives region variety through the stable API.

## Consequences

- Swapping PIA for another VPN provider is a gluetun configuration change, not a Lookout code change.
- Lookout talks to the VPN through a small rotator interface, so gluetun itself can also be replaced.
- An HTTP proxy carries only HTTP, and WebRTC's STUN goes around it and reveals the home IP. Browserless therefore launches every browser with `--webrtc-ip-handling-policy=disable_non_proxied_udp`, including for direct Runs. The similarly named `--force-webrtc-ip-handling-policy` does not work.
- gluetun runs its proxy with `HTTPPROXY_STEALTH=on`. Otherwise it forwards the Browserless container's address on plain-HTTP requests.
- Regions are kept to the US. A non-US exit changes what retailers serve (a country picker, another storefront), which would show up as false Changes.
- gluetun reports `running` even while it is stuck retrying a dead server, and it never moves off a dead server by itself. The rotator notes the current exit IP before stopping the VPN. A Rotation is complete only when `GET /v1/publicip/ip` returns an IP different from that one. gluetun picks servers at random without excluding the last one, so the same exit can come back. If the IP is unchanged, or none has arrived within about 15 s, the rotator rotates again, and VPN Runs stay paused meanwhile. At startup there is no previous IP, so any IP means the VPN is ready.
- gluetun's bundled PIA server list goes stale, so gluetun refreshes it periodically (`UPDATER_PERIOD`) into a persistent `/gluetun` volume.

Sources: [docs/research/script-execution-and-vpn.md](../research/script-execution-and-vpn.md), [docs/research/browser-and-vpn-prototype.md](../research/browser-and-vpn-prototype.md)
