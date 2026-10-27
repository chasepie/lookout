# Scripts are trusted and Direct Runs reach the LAN

Lookout treats Scripts as trusted code and accepts the network access below, rather than trying to contain a hostile Script. It protects how Scripts get in through the Account, login and first-launch setup, and asks users to review every Script they upload. This is the scope of a LAN-only homelab tool ([ADR-0002](0002-designed-for-lan-only-homelab-deployment.md)). ADR-0007 supersedes [ADR-0006](0006-browser-pages-reach-lookout-like-a-lan-device.md), which covered only pages reaching Lookout; [#46](https://github.com/chasepie/lookout/issues/46) records the decision and its evidence.

## Decision

- **Direct Runs reach the LAN like any browser on it.** A Script can navigate there, and a public page it loads can make its own requests or redirect there without the Script asking it to. This is accepted and documented. Users who need to keep those pages off the LAN can use VPN Watches or run Lookout on a separate VLAN or Docker host with access restricted to the devices they intend.
- **VPN Runs don't reach the LAN through their browser.** gluetun's firewall blocks that traffic. They can still reach the Docker host's published ports, including Lookout's anonymous endpoints, through gluetun's own Docker networks.
- **A Script's Node code is offline from the internet, but may reach the Docker host's published ports through the runner's internal networks on Linux Docker Engine.** This is expected from Docker's documentation, not verified. The Docker Desktop probes below tested browser and gluetun access, not Node-to-host access from the runner; assume the latter may be possible there too. It is documented, not prevented, and selecting a VPN Watch does not change the runner's networks.
- **Lookout's database and backend secrets are not mounted into the runner, and Script processes are not given the runner token or VPN-provider login**, such as the PIA user name and password, as described in [ADR-0003](0003-scripts-run-in-an-offline-runner-container.md). Optional gluetun HTTP proxy credentials (`Vpn__ProxyUsername` and `Vpn__ProxyPassword`) are different: they travel with the Run request into the Script's process to configure its browser, so Scripts can read them. This is accepted because Scripts are trusted. Those precautions and the Script's resource limits are not a promise to contain hostile code.
- **Pages in Direct Runs can reach Browserless's API, which requires a token.** The `/active` probe below confirmed a response at `localhost:3000` from the deployment of the time, which had no Browserless `TOKEN`. Since [#49](https://github.com/chasepie/lookout/issues/49), Browserless requires `LOOKOUT_BROWSERLESS_TOKEN` on every route (`STRICT_TOKEN_USE`), so such a page gets `401`, and the runner refuses to start with a `BROWSERLESS_URL` that has no token. Scripts are given the token, since they open their own browser with the runner's Browserless settings; their access to browser controls remains accepted under the trusted-Script decision.

Pages reaching Lookout still face its login, request-token header (XSRF) on state-changing requests, and `SameSite=Strict` cookies, which the browser a Script drives does not inherit from the user's Session. Without a login they get only anonymous access: the app's public files, `/health`, the session check, login and first-launch setup. Reaching a published port does not itself give a page access to the Account's Watches, Scripts or data.

## Evidence

The probes recorded in [#46](https://github.com/chasepie/lookout/issues/46) ran against a disposable deployment of `main` at `14e6359`, on Docker Desktop with Docker Engine 29.9.2, `linux/arm64`. A probe Script sent to the runner's `/execute` tried the LAN router at `192.168.1.1` and Browserless's API:

| Check | Direct Run | VPN Run |
| --- | --- | --- |
| Script navigates to `https://192.168.1.1` | Reached: `ERR_CERT_COMMON_NAME_INVALID` from the router's certificate | Timed out |
| A public page (`example.com`) `fetch`es the router | Reached, with the same certificate error: Chromium's local network protections did not block it in Browserless | Timed out |
| A public page redirects itself to the router | Reached | Timed out |
| A public page `fetch`es `http://localhost:3000/active` (Browserless's API, no `TOKEN`) | An opaque response came back | Answered with 500, apparently by gluetun |
| From the `gluetun` container, the `internet` network's gateway at `:8080/health` | Not tested in this check | `200 OK` from Lookout |

An opaque response does not reveal its status or body; the VPN 500 is not proof that Browserless answered. These observations record the tested deployment, not a guarantee about other versions or hosts.

gluetun allows traffic to the subnets of its own Docker networks so it can reach other containers ([inter-containers networking](https://github.com/qdm12/gluetun-wiki/blob/main/setup/inter-containers-networking.md), since v3.41; [`internal/firewall/enable.go`](https://github.com/qdm12/gluetun/blob/v3.41.3/internal/firewall/enable.go)). Each network's gateway is the Docker host, a consequence the wiki does not mention. Docker documents that containers on an internal bridge can communicate with its host address unless the gateway mode is `isolated` ([isolated gateway mode](https://docs.docker.com/engine/network/port-publishing/#gateway-mode-isolated)). That is the basis for the expected, unverified reach from the runner on Linux Docker Engine.

## Considered Options

All six restrictions were rejected as more than a LAN-only homelab tool with trusted Scripts needs:

- **An egress-filtering proxy**, such as [Smokescreen](https://github.com/stripe/smokescreen), as Browserless's only route out, with a second instance chained to gluetun for VPN Runs. Browserless would leave the `internet` network and every Run would get a proxy. Rejected for the extra services and because Direct Runs would lose UDP and WebRTC.
- **A firewall in Browserless's network namespace.** A sidecar with `NET_ADMIN` would hold the namespace, Browserless would join it with `network_mode: service:…`, and iptables would reject non-public destinations, with an `.env` allowlist. A prototype worked on Docker Desktop: the router and host gateway became `ERR_ADDRESS_UNREACHABLE` in Direct Runs while public sites and VPN Runs kept working. Rejected as an unnecessary restriction and extra deployment machinery under the trusted-Script decision. **If the decision is revisited, pick this option up.** The draft needed a name for gluetun only on `vpn-proxy`, since `gluetun` resolves to its `internet` address from Browserless.
- **Host firewall rules** in Docker's `DOCKER-USER` chain. Rejected because they live outside Compose, apply only on Linux, and need setting up by hand on every host.
- **Browserless's private-network navigation guard**, `getBlockedNetworkRanges()`. Rejected because it requires subclassing Browserless's configuration in a custom build and filters in the browser rather than at the network boundary.
- **gluetun's `/iptables/post-rules.txt`**, to keep VPN Runs off Lookout's published port. Rejected as unnecessary: reaching that port gives pages only Lookout's anonymous endpoints.
- **Isolated gateways** on internal networks, using `com.docker.network.bridge.gateway_mode_ipv4: isolated`. Rejected because preventing the runner reaching the host's published ports isn't needed when Scripts are trusted, and this would require Docker 28 or later.

## Consequences

- Users must review every Script before uploading it, including a new Script version that starts a Test run. LAN access by pages is accepted even when a reviewed Script only visits public sites.
- A strong Account password matters: pages and LAN devices can attempt login. Since [#48](https://github.com/chasepie/lookout/issues/48), 5 failed logins from an address refuse its logins for the rest of a 5-minute window, which makes guessing a strong password impractical. First-launch setup is open to whoever reaches it first until an Account exists, including another deployment on the same host. Since [#47](https://github.com/chasepie/lookout/issues/47), Lookout sets the Account from `LOOKOUT_USERNAME` and `LOOKOUT_PASSWORD` on every start, before it accepts requests, so an install with both set never has setup open. Setup by someone else remains a risk only for an install set up without these settings, whose users must set up the Account straight after first start.
- [Security considerations](../user/security-considerations.md) explain the trust decision, network access and deployment precautions to users. README's smoke test checks LAN access in Direct and VPN Test runs as well as anonymous access to Lookout, so a change in exposure is noticed.
