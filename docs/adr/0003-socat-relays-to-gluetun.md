# The Backend reaches gluetun through relays, not a shared network

In production the Backend shares no network with gluetun, and reaches gluetun's control server and HTTP proxy through two `socat` relays on networks of
their own. gluetun's firewall lets it open connections to every Docker network it's on, and its proxy follows suit, so anything sent through the proxy,
such as a page loaded in a VPN run, could reach a Backend on one of those networks by IP; we confirmed this against gluetun's firewall rules, which
don't depend on the tunnel being up. A page that reaches the relays leads only back to gluetun's own ports, which need its key or the proxy's password.

## Considered Options

- **The Backend on a network with gluetun**, relying on its sign-in: two fewer containers, but the Backend exposed to every website a VPN run visits.
- **`network_mode: service:gluetun`**, the usual gluetun setup: it would send browserless's runs without the VPN through it too.
