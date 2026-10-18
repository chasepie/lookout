# gluetun for VPN rotation

VPN Runs go through gluetun's HTTP proxy, set on each Playwright browser context, using PIA over OpenVPN with several regions listed in `SERVER_REGIONS`. Lookout rotates the exit IP every 10–15 minutes, or early after a Known failure, by reconnecting gluetun through its authenticated control API (`PUT /v1/vpn/status`). This needs no Docker socket and leaves Browserless untouched. Direct Runs share the same Browserless instance without a proxy.

## Considered Options

- **`thrnz/docker-wireguard-pia`.** Rejected even though it was the familiar choice and uses WireGuard. Its regions have only 1–3 servers, so restarting gives little IP variety. Changing region means recreating the container, which needs the Docker socket and can break containers that share its network.
- **Routing Browserless through gluetun's network (`network_mode: service:gluetun`).** Rejected because every Run would go through the VPN, and a gluetun restart would disrupt the browser.
- **gluetun's `PUT /v1/vpn/settings` to switch regions.** Avoided because it is marked experimental. Listing several regions and reconnecting gives region variety through the stable API.

## Consequences

- Swapping PIA for another VPN provider is a gluetun configuration change, not a Lookout code change.
- Lookout talks to the VPN through a small rotator interface, so gluetun itself can also be replaced.
