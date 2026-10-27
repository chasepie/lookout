# Pages Browserless loads can reach Lookout like any LAN device

Browserless's Chromium shares no Docker network with Lookout, but the pages it loads can still reach Lookout through the Docker host, at the port Lookout is published on, both in Direct Runs and through gluetun's proxy in VPN Runs (found by the [#42 smoke test](../research/vpn-smoke-test.md), [#44](https://github.com/chasepie/lookout/issues/44)). We accept this rather than restrict it: Lookout is designed for a LAN where other devices can reach it ([ADR-0002](0002-designed-for-lan-only-homelab-deployment.md)), and to Lookout such a page is one more of them. Its login, its request-token header (XSRF) on every state-changing request, and its `SameSite=Strict` cookies, which Browserless's pages never have, still apply, so a page gets only what needs no login: `/health`, the session check, login and first-launch setup.

## Considered Options

- **Block the Docker host from Browserless and gluetun's proxy**, with host firewall rules or a gluetun firewall setting. Rejected. A Direct Run's browser has to reach the internet, and from the host's network it reaches the LAN, and so the host's LAN address and Lookout's published port, anyway. Closing that too means firewall rules on the host, which differ between Docker Desktop and Linux Docker Engine and which Compose can't express, and blocking the LAN would stop Watches on LAN pages. It would close one route to an endpoint that every LAN device can already reach.

## Consequences

- A page Browserless loads, such as one a Script visits or one that redirects or embeds another, can do what any LAN device can:
  - **Guess the password.** Login has no rate limiting or lockout (ADR-0002's list of what internet exposure would need). A strong password is the defence.
  - **Set up the Account on a fresh install before its owner does.** Setup is open until an Account exists, to whoever reaches it first. A fresh install has no Runs of its own, but another browser that reaches the host does, such as another Lookout, or a test deployment, on the same host. Set up the Account straight after the first `docker compose up`.
- Docs mustn't claim Browserless can't reach Lookout. They say it shares no network with Lookout but can reach its published port through the host, and README's smoke test checks that a page gets `Healthy` from `/health` through the host, so that any change in this exposure is noticed.
- If Lookout is ever hardened for wider exposure (ADR-0002), login rate limiting closes the first risk for pages and LAN devices alike.
