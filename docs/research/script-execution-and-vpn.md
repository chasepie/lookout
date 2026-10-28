# Lookout: script-execution research (facts as of 2026-10-18)

Scope: running user-written Playwright (Node.js, Playwright 1.64) scripts on a schedule from a .NET 10 backend, deployed with Docker. These are facts from primary sources, with trade-offs noted. "Source:" means I read the repo code myself (browserless `main` @ `2185e9b`, v2.57.1, 2026-10-16; steel-browser `main`, shallow clone 2026-10-18).

**Current decision:** [ADR-0007](../adr/0007-scripts-are-trusted-and-direct-runs-reach-the-lan.md) treats Scripts as trusted code. The sandbox options below are historical research, not a promise to contain hostile Scripts. Internal Docker networks remove the runner's direct internet route; they may still allow it to reach the host's published ports. Direct Runs and their pages can reach the LAN.

---

## 1. Browserless (browserless/browserless, self-hosted)

### Licence
- The project is dual-licensed: `SPDX-License-Identifier: SSPL-1.0 OR Browserless Commercial License` ([LICENSE](https://github.com/browserless/browserless/blob/main/LICENSE), [README#licensing](https://github.com/browserless/browserless#licensing)). GitHub's About blurb says "Free for non-commercial uses."
- The README says a commercial licence is needed to "build commercial sites, applications, or in a continuous-integration system that's closed-source." Open-source apps "under a license compatible with the Server Side License 1.0" may use it under SSPL terms ([README](https://github.com/browserless/browserless#licensing)).
- SSPL §2 "explicitly affirms your unlimited permission to run the unmodified Program, subject to section 13". §13 obligations (release the full "Service Source Code") apply only if you "make the functionality of the Program ... available to third parties as a service" ([SSPL text](https://www.mongodb.com/legal/licensing/server-side-public-license)).
- **Net:** a private, single-user, non-commercial deployment of the unmodified image fits both the README's "free for non-commercial" statement and SSPL §2. A commercial licence comes into play if Lookout becomes commercial or closed-source CI, or is offered to third parties as a service. This is not legal advice.
- The open-source image excludes several features: "Stealth and CAPTCHA solving", `/unblock`, `/smart-scrape`, `/crawl`, BrowserQL, session recording, the live debugger, and managed proxies ([Open Source Docker Deployment](https://docs.browserless.io/enterprise/open-source)). Images: `ghcr.io/browserless/{chromium,chrome,firefox,webkit,edge,multi}`.

### How Playwright clients connect
- **Playwright protocol (`browserType.connect`)**: WebSocket routes `/chromium/playwright` (alias `/playwright/chromium`), plus Firefox and WebKit equivalents. Example from the README: `pw.firefox.connect('ws://localhost:3000/firefox/playwright')` ([README](https://github.com/browserless/browserless)). The route rejects clients whose User-Agent does not contain "playwright" (source: `src/shared/chromium.playwright.ws.ts`, `src/http.ts`).
- **CDP (`chromium.connectOverCDP`)**: connect to the root `ws://host:3000?token=…`. The hosted docs call CDP "the primary and recommended way" ([Connecting Playwright](https://docs.browserless.io/baas/connect-playwright)). Playwright's own docs say CDP is "significantly lower fidelity than the Playwright protocol connection" and Chromium-only ([BrowserType API](https://playwright.dev/docs/api/class-browsertype)).
- **Version matching:**
  - Playwright requires that "the major and minor version of the Playwright instance that connects needs to match" the server for `connect()` ([BrowserType API](https://playwright.dev/docs/api/class-browsertype)).
  - Browserless bundles several `playwright-core` versions and chooses one from the client's `User-Agent` (`/Playwright\/(\d+\.\d+)/`). Any unmatched version falls back to the default (source: `package.json` `playwrightVersions`, `src/utils.ts:70`, `src/config.ts loadPwVersion`).
  - **v2.57.1 (2026-10-16) supports playwright-core 1.64.0, 1.63.0, 1.62.1, 1.61.1, 1.60.0**, keeping a rolling window of 5 minor versions ([CHANGELOG](https://github.com/browserless/browserless/blob/main/CHANGELOG.md), [PR #5620](https://github.com/browserless/browserless/pull/5620)). **Playwright 1.64 is supported as of this release.**
  - The docs' [Version Compatibility page](https://docs.browserless.io/baas/versions) is stale (it lists 1.52–1.57). Use the CHANGELOG or the `/meta` endpoint instead.
  - `connectOverCDP` "generally works across versions" ([versions page](https://docs.browserless.io/baas/versions)).
  - Third-party reports show HTTP 428 errors when the client is outside the supported window ([Railway thread](https://station.railway.com/questions/mismatch-between-browserless-chrome-and-0001cd38)).
- **Trade-offs between the two modes** ([Playwright customizations](https://docs.browserless.io/baas/advanced-configurations/playwright-customizations)):
  - Stealth and extension features need CDP mode.
  - Native `connect` mode is Playwright's full-fidelity protocol.
  - The page also claims CDP mode lacks `page.route()` and `APIRequestContext`. This contradicts upstream Playwright, where those generally work over CDP. **Unverified.**

### `/function` API: where user code runs
- `POST /function` (alias `/chromium/function`) takes JS or JSON `{code, context}`. The code is Puppeteer-style (`export default async ({ page, context }) => …`), not Playwright ([Function API](https://docs.browserless.io/rest-apis/function)).
- **The code runs inside the browser's JavaScript runtime, not Node:**
  - From the v2 migration guide: "We no longer run /function calls in the NodeJS environment, and instead run inside the browser's JavaScript runtime." Also "CommonJS no longer supported" and "npm packages are no longer supported". `import` loads ESM over HTTP, e.g. from unpkg ([Migration guide](https://docs.browserless.io/baas/migrate)).
  - Source confirms this: Browserless opens a page, serves the user code as a JS module through request interception, and runs it in-page with a browser-bundled Puppeteer client that connects back over WebSocket (`src/shared/utils/function/handler.ts`).
  - **Consequence:** there are no Node built-ins, no `fs`, and no `playwright` import. Isolation is whatever Chromium's renderer sandbox provides. The page can make network requests (fetch, remote `import`) and drives the whole browser through Puppeteer.
- Security note from the docs: without `TOKEN`, "every endpoint is left unauthenticated, including /function, which runs arbitrary Puppeteer code" ([Open Source Docker Deployment](https://docs.browserless.io/enterprise/open-source)). v2.57.0 added `STRICT_TOKEN_USE` to enforce the token on every route ([CHANGELOG](https://github.com/browserless/browserless/blob/main/CHANGELOG.md)).
- **For Lookout:** if scripts must be Node and Playwright (`import { chromium } from 'playwright'`), they run on the client side, in your own Node runner. Browserless only hosts the browser.

### Headful, stealth, proxy
- **Headful:**
  - The image installs Xvfb, and `scripts/start.sh` starts `Xvfb :99` when `DISPLAY` is unset (source: `docker/base/Dockerfile`, `scripts/start.sh`).
  - Launch options accept `headless: false`, passed as `?headless=false` or in `&launch={…}` JSON ([Launch Parameters](https://docs.browserless.io/rest-apis/launch-parameters); source `src/types.ts`).
  - The v1 env var `DEFAULT_HEADLESS` was removed in v2 ([Migrate from V1](https://docs.browserless.io/enterprise/migrate-from-v1)).
- **Stealth:**
  - The docs list "Stealth and CAPTCHA solving" as excluded from open source ([open-source page](https://docs.browserless.io/enterprise/open-source)).
  - However, the open-source code has a `stealth?: boolean` launch option that launches through `@zorilla/puppeteer-extra-plugin-stealth`. It applies only to the CDP/Puppeteer-launched browser path, not the `/chromium/playwright` route (source: `src/types.ts` `CDPLaunchOptions`, `src/browsers/browsers.cdp.ts`).
  - The likely reading is that the basic puppeteer-extra stealth plugin is present, while the advanced fingerprint and CAPTCHA stealth routes are paid. **I could not confirm this from the docs.**
- **Proxy:**
  - Bring your own proxy per session with launch args such as `--proxy-server`; managed proxies are cloud-only ([open-source page](https://docs.browserless.io/enterprise/open-source)).
  - The Playwright launch options also accept `proxy: {server, username, password, bypass}` (source: `src/types.ts` `BrowserServerOptions`).
  - Playwright itself supports per-context `browser.newContext({ proxy })` ([Playwright network docs](https://playwright.dev/docs/network)). Over CDP, a context proxy does not apply to the default context ([customizations](https://docs.browserless.io/baas/advanced-configurations/playwright-customizations)).

### Concurrency, queueing, timeouts ([Docker configuration](https://docs.browserless.io/enterprise/docker/configuration); source `src/config.ts`)
| Env var (alias) | Default | Meaning |
|---|---|---|
| `CONCURRENT` (`MAX_CONCURRENT_SESSIONS`) | 10 | Max concurrent sessions; extra requests queue |
| `QUEUED` (`QUEUE_LENGTH`) | 10 | Max queued requests, then HTTP 429 |
| `TIMEOUT` (`CONNECTION_TIMEOUT`) | 30000 ms | Session timeout; `-1` disables it |
| `TOKEN` | none | Auth; unauthenticated if unset |
| `HEALTH`, `MAX_CPU_PERCENT`, `MAX_MEMORY_PERCENT` | false / 99 / 99 | Reject new sessions when overloaded |
| `ALLOW_FILE_PROTOCOL`, `DISABLE_BLOCKLIST` | false / false | `file://` access and the URL blocklist |

The docs also advise `shm_size: "2g"` ([open-source page](https://docs.browserless.io/enterprise/open-source)). Checked in #50: with Browserless v2.57.1 (Playwright 1.64's Chromium 156, build 1248), Chromium shares memory through memfd (`/memfd:shared-memory-region`), not `/dev/shm`. That holds with or without the `--disable-dev-shm-usage` flag Playwright adds by default (removed through `ignoreDefaultArgs` in Browserless's `launch` parameter, and confirmed absent from the running Chromium's command line), so `/dev/shm` stayed empty under load, while the cgroup's shared memory reached 408 MB.

---

## 2. Alternatives (self-hosted remote browser for Playwright)

| Option | Licence | Connect method | What it provides | Notable limits |
|---|---|---|---|---|
| **Playwright `run-server` / `launchServer`** in `mcr.microsoft.com/playwright:v1.64.0-noble` (also `-jammy`, `-resolute`) | Apache-2.0 (Playwright) | `chromium.connect('ws://host:3000/')` after `npx -y playwright@1.64.0 run-server --port 3000 --host 0.0.0.0` ([Playwright Docker docs](https://playwright.dev/docs/docker)) | Full-fidelity Playwright protocol; Chromium, Firefox and WebKit; exact version match by construction | Client major.minor must match ([BrowserType](https://playwright.dev/docs/api/class-browsertype)). No auth, queueing, stealth or session management. Docs say the image is "intended to be used for testing and development purposes only" and "not recommended ... to visit untrusted websites". Running as root disables the Chromium sandbox; use `pwuser` plus the seccomp profile; `--ipc=host` is recommended ([Docker docs](https://playwright.dev/docs/docker)) |
| **Steel** (`steel-dev/steel-browser`, `ghcr.io/steel-dev/steel-browser`) | Apache-2.0 ([repo](https://github.com/steel-dev/steel-browser)) | CDP: `POST /v1/sessions` returns a `websocketUrl`, then `connectOverCDP` | Session API, per-session `proxyUrl`, ad blocking, "stealth plugins and fingerprint management" (deps: `fingerprint-generator` / `fingerprint-injector`), `CHROME_HEADLESS` env (default true), debug UI on port 9223 ([README](https://github.com/steel-dev/steel-browser), [self-host docs](https://docs.steel.dev/overview/self-hosting/docker)) | "Public beta"; latest tag `v0.5.4-beta` (2026-08-25). **One `activeSession` per container** (source: `api/src/services/session.service.ts`), so concurrency means multiple containers. CDP only, so lower fidelity than the Playwright protocol |
| **chromedp/headless-shell** (`docker.io/chromedp/headless-shell`) | MIT ([repo](https://github.com/chromedp/docker-headless-shell)) | Raw CDP on 9222, `connectOverCDP` | Minimal Chrome headless-shell, daily stable/beta/dev tags, UA patched to look like Chrome | Headless only; no auth, queue, proxy API or stealth; needs `--shm-size`; seccomp profile for non-root |
| **Browserless** (above) | SSPL / commercial | Both `connect` (pinned versions 1.60–1.64) and CDP | Auth token, concurrency and queue, health checks, multiple browsers, REST APIs, Xvfb headful, basic stealth flag on the CDP path | SSPL; the advanced stealth and CAPTCHA features are paid |

I found no actively maintained, widely adopted browserless *fork* in primary sources. Search turned up small forks (e.g. OudyWorks/browserless), but I did not evaluate them.

---

## 3. Sandboxing untrusted Node.js code (built-ins + `playwright` only)

### Node permission model
- **Stability:** `Stability: 2 - Stable`; "v23.5.0, v22.13.0: This feature is no longer experimental" ([Permissions, Node v26.12.0](https://nodejs.org/api/permissions.html)). The Node 24 CLI docs (v24.22.0) also mark the Permission Model and `--allow-fs` flags stable ([v24 CLI](https://nodejs.org/docs/latest-v24.x/api/cli.html)).
- **Flags:** `--permission`, `--permission-audit`, `--allow-fs-read`, `--allow-fs-write`, `--allow-child-process`, `--allow-worker`, `--allow-addons`, `--allow-wasi`, `--allow-net`, `--allow-ffi`, `--allow-openssl-store`, and (v24) `--allow-inspector` ([Permissions](https://nodejs.org/api/permissions.html)).
- **Network:**
  - `--allow-net` was "Added in: v25.0.0", "Stability: 1.1 - Active development". Under `--permission`, network access throws `ERR_ACCESS_DENIED` unless `--allow-net` is passed ([CLI](https://nodejs.org/api/cli.html#--allow-net)).
  - **It is not in Node 24's docs, so Node 24 cannot restrict network through the permission model; Node 26 can.**
  - It is all-or-nothing, with no per-host allow-list documented. A Playwright client needs the network to reach a remote browser, so `--allow-net` would have to be on.
- **Not a security boundary:** "It does not provide security guarantees in the presence of malicious code. Malicious code can bypass the permission model..." ([Permissions](https://nodejs.org/api/permissions.html)). Documented gaps:
  - Symlinks are followed outside granted paths.
  - Existing file descriptors bypass checks.
  - Workers spawned with explicit `execArgv` escape the model.
  - `process._debugProcess(pid)` can open the inspector of another same-user Node process.
- **Trade-off:** launching a browser locally from Playwright needs `--allow-child-process`, which largely defeats the model. Connecting to a remote browser avoids this.

### `node:vm`
- "**The `node:vm` module is not a security mechanism. Do not use it to run untrusted code.**" ([vm docs](https://nodejs.org/api/vm.html)). `vm.SourceTextModule` is "Stability: 1 - Experimental" and needs `--experimental-vm-modules`.

### isolated-vm
- "currently in _maintenance mode_"; supported "for as long as is technically feasible" ([repo](https://github.com/laverdet/isolated-vm)).
- Compatibility: Node 24 → isolated-vm 6.x/5.x; Node 26 → 7.x. Needs `--no-node-snapshot` on Node ≥20.
- Warnings: "Running untrusted code is an extraordinarily difficult problem"; do not leak `Reference` / `ExternalCopy` objects; run it in a separate process.
- **Trade-off (inference):** an isolate is a bare V8 context with no Node runtime, so `import 'playwright'` (which needs `net`, `child_process`, etc.) cannot run inside it. You would have to expose a hand-written bridge of Playwright operations through References, which is a large attack surface.

### Container-level isolation (Docker primitives)
- `docker run` flags: `--read-only` ("mounts the container's root filesystem as read only"), `--tmpfs`, `--cap-drop`, `--security-opt no-new-privileges`, `--pids-limit`, `--memory`, `--runtime` ([docker run reference](https://docs.docker.com/reference/cli/docker/container/run/)).
- A Compose network with `internal: true` "lets you create an externally isolated network" ([Compose networks](https://docs.docker.com/reference/compose-file/networks/)). Putting the runner on a network that contains only the browser service, and not the backend or DB, keeps it off the backend's network, but does not by itself prevent access to published ports through the Docker host. Secrets stay out if you do not pass env vars or mounts.
- gVisor adds a user-space kernel runtime: `docker run --runtime=runsc` ([gVisor Docker quick start](https://gvisor.dev/docs/user_guide/quick_start/docker/)).
- **Trade-offs:**
  - A container per run costs startup time (Node plus Playwright client, around seconds) but gives the strongest boundary of these options.
  - The backend needs a way to launch containers: a Docker socket, which is effectively root on the host, or a dedicated launcher.
  - A long-lived locked-down runner container is cheaper but shares state between runs.

---

## 4. Routing scraper traffic through PIA with gluetun

- **gluetun** (`qmcgaw/gluetun`, MIT; latest release v3.41.3, 2026-07-30) is a VPN-client container. It provides a kill-switch firewall, DNS over TLS/HTTPS, and built-in HTTP/Shadowsocks proxies ([repo](https://github.com/qdm12/gluetun), [releases](https://github.com/qdm12/gluetun/releases)). In practice it is the de facto approach for this; there is no "official standard".
- **PIA support** ([wiki: PIA](https://github.com/qdm12/gluetun-wiki/blob/main/setup/providers/private-internet-access.md)):
  - **OpenVPN is native.** Required env: `VPN_SERVICE_PROVIDER=private internet access`, `OPENVPN_USER`, `OPENVPN_PASSWORD`. Optional: `SERVER_REGIONS`, `SERVER_NAMES`, `PORT_FORWARD_ONLY`, `OPENVPN_PROTOCOL`, and others. Needs `cap_add: NET_ADMIN` and `/dev/net/tun`.
  - **WireGuard is not native:** "For now, native support cannot be added, but this is a slow work in progress." The workaround is to generate a config with `kylegrantlucas/pia-wg-config` and use the custom provider.
  - **Port forwarding:** `VPN_PORT_FORWARDING=on`; the port is kept for 60 days if `/gluetun` is bind-mounted. The author warns PIA port forwarding "is not really working ... seems to only work for p2p applications". Lookout's scraping is outbound-only, so this does not matter for it.
  - v3.41.2 changed PIA defaults (AES-GCM for all presets, default OpenVPN ports 8080/UDP and 8443/TCP) ([release notes](https://github.com/qdm12/gluetun/releases)).
- **Attaching containers** ([wiki: connect a container](https://github.com/qdm12/gluetun-wiki/blob/main/setup/connect-a-container-to-gluetun.md)): `network_mode: "service:gluetun"` in the same compose file, or `network_mode: "container:gluetun"` / `--network=container:gluetun` from outside.
- **Caveats for containers that share gluetun's network namespace:**
  - Compose rejects a `networks:` key on a service that sets `network_mode` ([Compose services](https://docs.docker.com/reference/compose-file/services/)). The container gets only gluetun's networks.
  - Ports must be published on the **gluetun** service, not on the attached container ([wiki: port mapping](https://github.com/qdm12/gluetun-wiki/blob/main/setup/port-mapping.md)).
  - Attached containers share `localhost` and cannot listen on the same port ([wiki: inter-container networking](https://github.com/qdm12/gluetun-wiki/blob/main/setup/inter-containers-networking.md)).
  - **Reaching other services such as the backend:**
    - Other containers reach an attached container at `gluetun:<port>`.
    - The attached container reaches other containers on gluetun's network by name only "since v3.41".
    - Private subnets outside the tunnel need `FIREWALL_OUTBOUND_SUBNETS`. Any hostname that resolves to a private IP must also be listed in `DNS_REBINDING_PROTECTION_EXEMPT_HOSTNAMES`, because "gluetun blocks dns entries pointing to private IP addresses by default" ([wiki: firewall](https://github.com/qdm12/gluetun-wiki/blob/main/setup/options/firewall.md)).
  - Restarting or recreating gluetun breaks the namespace for attached containers. This is commonly reported, but **I did not verify it in the wiki**.
- **Alternative to sharing the namespace:**
  - Enable gluetun's HTTP proxy (`HTTPPROXY=on`, `:8888`, optional user/password) ([wiki: http-proxy](https://github.com/qdm12/gluetun-wiki/blob/main/setup/options/http-proxy.md)). Then point only the browser at it with Playwright's `proxy` option (launch or per-context; HTTP(S) and SOCKSv5 supported per [Playwright network docs](https://playwright.dev/docs/network)) or Browserless `--proxy-server`.
  - This keeps the browser and runner on normal Docker networks and lets you route per job.
  - A `SOCKS5_ENABLED` option is documented in the wiki ([socks5](https://github.com/qdm12/gluetun-wiki/blob/main/setup/options/socks5.md)) but does not appear in any release notes, so it is likely only in `:latest`/master. **Unverified for v3.41.3.**

---

# Follow-up: VPN rotation

Goal: rotate the PIA exit IP every 10–15 minutes, from the app, without giving the app full access to the Docker socket. Some Runs use the VPN and others go direct; the browser is `ghcr.io/browserless/chromium` driven by Playwright `chromium.connect`. "Source:" means I read the code: thrnz `master` (shallow clone 2026-10-18); gluetun tag `v3.41.3`; browserless `main` @ `2185e9b` (v2.57.1); wollomatic/socket-proxy `main`.

## F1. thrnz/docker-wireguard-pia

- **Maintenance:** active. The last commit is 2026-09-27 ("Fix reconnecting when the endpoint ip remains unchanged"). There are no GitHub releases; the README says "Images are rebuilt monthly". No licence file is detected ([repo](https://github.com/thrnz/docker-wireguard-pia)).
- **Location choice:** set by the `LOC` env var, e.g. `us_california` or `swiss`. Ids come from [PIA's server list](https://serverlist.piaservers.net/vpninfo/servers/v6). "Multiple ids can be listed, separated by either a space or a comma, and are used as **fallback** if the initial endpoint registration fails" ([README](https://github.com/thrnz/docker-wireguard-pia#config)). The `run` script tries each LOC in order (`for location in ${LOC//,/ }`). This is fallback order, **not random selection among regions** (source: `run` lines 383–417). `PIA_IP` / `PIA_CN` / `PIA_PORT` pin a specific server.
- **Does a plain restart change the IP?**
  - On each start, `wg-gen.sh` re-downloads the server list and picks a random WireGuard server *within* the region: "Some locations have multiple servers available. Pick a random one." (`serverindex=$(( RANDOM % totalservers))`, source: `extra/wg-gen.sh` lines 180–201). So `docker restart` with the same env can land on a different server/IP in the **same** region.
  - The pool is small. The live server list on 2026-10-18 has 214 WireGuard regions: 7 have 1 server, 100 have 2, and 107 have 3 (my count from `serverlist.piaservers.net/vpninfo/servers/v6`). A restart therefore has a 1-in-1 to 1-in-3 chance of the same server.
  - **Changing region needs a new `LOC`, which means recreating the container.** Env vars cannot change on `docker restart`.
  - Whether one PIA server maps to one stable exit IP is **unverified**.
- **Proxy:** none is shipped (grep for proxy/socks in `run`, `extra/`, `Dockerfile` finds nothing). The documented way to share the tunnel is `--net=container:xyz` / `network_mode: service:xyz`. There is also an advanced gateway mode (`FWD_IFACE`, `PF_DEST_IP`) ([README](https://github.com/thrnz/docker-wireguard-pia#config)).
- **What happens to sharing containers when it restarts:** "When the VPN container is brought down or recreated, Docker appears to bring down the shared network with it, so other containers may also require recreating to regain network access" ([README#networking](https://github.com/thrnz/docker-wireguard-pia#networking)).
- **In-place reconnect:** `RECONNECT=1` re-registers keys "without the WireGuard interface being brought down", which is "mostly transparent to other containers". It triggers only when the endpoint is detected as unresponsive; there is no on-demand trigger or API ([README#networking](https://github.com/thrnz/docker-wireguard-pia#networking)).

## F2. gluetun control server (HTTP API)

- **What the docs say:** "The HTTP control server allows to obtain and modify the state of the container without restarting it." It listens on `:8000` ([wiki: control server](https://github.com/qdm12/gluetun-wiki/blob/main/setup/advanced/control-server.md)). Documented VPN routes:
  - `GET /v1/vpn/status`
  - `PUT /v1/vpn/status` with `{"status":"running"|"stopped"}`
  - `GET /v1/vpn/settings`
  - `GET /v1/publicip/ip`, which returns `{"public_ip":"…"}` and is useful for confirming that the IP changed.
  - The older `/v1/openvpn/status` (GET/PUT) and `/v1/openvpn/settings` (GET) are still in the route table (source: `internal/server/middlewares/auth/settings.go`).
- **Changing server or region at runtime:** `PUT /v1/vpn/settings` exists in code. Its JSON body is merged onto the current settings with `OverrideWith`, validated, and then applied by `SetSettings`. If anything changed, the VPN is stopped and started again *inside the container*; otherwise it returns "settings left unchanged" (source: `internal/server/vpn.go` `patchSettings`, `internal/vpn/state/vpn.go`).
  - The body uses the settings JSON tags, e.g. `{"provider":{"server_selection":{"regions":["Netherlands"]}}}` (source: `internal/configuration/settings/provider.go`, `serverselection.go`).
  - It was added in commit `0bb32006` (2022-08-21), first in tag **v3.31.0**. The commit message says "Undocumented, experimental for now", and the wiki still documents only GET for settings.
  - **Inference, not verified:** settings changed through the API are held in memory, so a container restart reverts to the env vars.
- **Forcing a reconnect:**
  - `PUT /v1/vpn/status` `stopped` then `running`, or any changed `PUT /v1/vpn/settings`, restarts the VPN loop.
  - The loop calls `GetConnection(...)` on every (re)start (source: `internal/vpn/run.go`, `openvpn.go`, `wireguard.go`).
  - `pickConnection` "picks a random connection from the pool of connections", meaning all servers matching the filters, unless an endpoint IP is pinned (source: `internal/provider/utils/pick.go`, used by `privateinternetaccess/connection.go`).
  - **So with `SERVER_REGIONS` listing several regions, each reconnect picks a random server from the combined pool.** Regions with more servers are proportionally more likely. Repeats are possible because nothing excludes the previous server.
  - The container and its network namespace stay up, so containers attached with `network_mode: service:gluetun` keep their namespace.
- **Authentication:**
  - v3.39.1 (2024-09-29): "require authentication for vulnerable routes (#2434)".
  - v3.40.0 (2024-12-25): a role-based auth system with methods `none`, `apikey` (`X-API-Key` header) and `basic`, configured via a TOML file at `/gluetun/auth/config.toml` ([releases](https://github.com/qdm12/gluetun/releases)).
  - v3.41.0 (2025-12-25): `HTTP_CONTROL_SERVER_AUTH_DEFAULT_ROLE`, as JSON.
  - The current wiki says "All routes are now private by default and **require** authentication" ([wiki](https://github.com/qdm12/gluetun-wiki/blob/main/setup/advanced/control-server.md)).
  - Roles can be scoped per route, e.g. `routes = ["PUT /v1/vpn/settings", "GET /v1/publicip/ip"]`, with an API key.
- **HTTP proxy across reconnects:**
  - The HTTP proxy runs in its own loop, started once in `main.go` alongside the VPN loop. It dials destinations with a plain `net.Dialer` through the namespace's routing, i.e. the tunnel (source: `cmd/gluetun/main.go` ~L456, `internal/httpproxy/https.go`).
  - The listener therefore survives a VPN reconnect.
  - **Inference, not verified:** in-flight connections drop, and new ones fail while the tunnel is down because the kill-switch firewall blocks egress. Requests work again once the tunnel is up. Schedule rotation between Runs, then poll `GET /v1/vpn/status` and `/v1/publicip/ip` before starting the next VPN Run.

## F3. Browserless per-session proxy with `chromium.connect` to `/chromium/playwright`

Each WebSocket connection to Browserless launches its own browser, so launch options apply per connection.

There are three ways to pass a proxy (source: `src/browsers/index.ts` ~L680–800):

1. **Query param `--proxy-server=<url>`**, e.g. `ws://browserless:3000/chromium/playwright?token=…&--proxy-server=http://gluetun:8888`. This is also documented with the CDP route ([proxies recipe](https://docs.browserless.io/baas/recipes/proxies)).
   - It replaces any `--proxy-server` in `launch.args`.
   - For `/playwright` paths, Browserless then **removes the arg and sets `launchOptions.proxy = { server }`**, passed to Playwright `launchServer`.
   - **Caveat:** this path carries only the server, never username/password. It also overwrites any `proxy` object you put in `launch`.
2. **`launch` JSON**, as JSON or base64 JSON in the `launch` query param: `&launch=${encodeURIComponent(JSON.stringify({ proxy: { server: 'http://gluetun:8888', username, password, bypass } }))}`.
   - `BrowserServerOptions.proxy` supports `server`, `username`, `password` and `bypass`, and is passed through to `playwright.launchServer()` (source: `src/types.ts`).
   - Do **not** also send `--proxy-server`, or option 1 overwrites this object.
3. **Client-side per-context proxy:** `browser.newContext({ proxy: { server, username, password } })` over the Playwright protocol ([Playwright network docs](https://playwright.dev/docs/network); also shown in [Browserless Playwright configurations](https://docs.browserless.io/baas/advanced-configurations/playwright-customizations)).
   - This lets one Browserless instance serve both VPN and direct Runs: direct Runs omit the proxy.
   - The "context proxy doesn't apply to the default context" caveat applies to `connectOverCDP`, not `connect`.

Other caveats:
- `externalProxyServer`, the documented credentials-in-URL parameter ([proxies](https://docs.browserless.io/baas/features/proxies)), **does not exist in the open-source code** (0 matches in `src/`), so it is cloud/enterprise only.
- The CDP launch path adds Browserless's own host to `--proxy-bypass-list` automatically (source: `src/browsers/browsers.cdp.ts` ~L402). I found no equivalent for the Playwright path, and it is not needed because the client does not route through the browser.
- Gluetun's `HTTPPROXY_USER` / `HTTPPROXY_PASSWORD` pair with options 2 or 3.
- DNS for proxied requests resolves at the proxy (gluetun) for HTTP CONNECT. This is standard proxy behaviour and not separately verified.

## F4. Scoped Docker socket proxies

- **Tecnativa/docker-socket-proxy** (Apache-2.0, v0.5.0 2026-07-27, HAProxy-based):
  - Access is granted by API section via env flags (`CONTAINERS`, `POST`, `ALLOW_RESTARTS`, and others) ([README](https://github.com/Tecnativa/docker-socket-proxy)).
  - The shipped rule is `^(/v[\d\.]+)?/containers/[a-zA-Z0-9_.-]+/((stop)|(restart)|(kill))` when `ALLOW_RESTARTS` is on, and non-GET requests are denied unless `POST=1` ([haproxy.cfg](https://github.com/Tecnativa/docker-socket-proxy/blob/master/haproxy.cfg)).
  - **It cannot be scoped to one named container through configuration;** `ALLOW_RESTARTS` lets the client stop, restart or kill *any* container. Narrowing it would mean maintaining your own `haproxy.cfg`, which the README does not document.
- **wollomatic/socket-proxy** (MIT, 1.13.1 2026-08-15, Go, from-scratch image):
  - "The allowlist is configured for each HTTP method separately using the Go regexp syntax." `^` and `$` "are added automatically" ([README](https://github.com/wollomatic/socket-proxy)).
  - The regex matches `r.URL.Path` only, so query strings like `?t=10` are not part of the match (source: `cmd/socket-proxy/handlehttprequest.go`).
  - **It can restrict access to one container, e.g. `-allowPOST=/v1\.[0-9]+/containers/lookout-vpn/(restart|start|stop)`**, and optionally `-allowGET=/v1\.[0-9]+/containers/lookout-vpn/json`.
  - It also offers `-allowfrom=<hostname|CIDR>`, per-client allowlists via container labels (`socket-proxy.allow.post=…`), and `-allowbindmountfrom`.
  - Caveat (inference): the Docker API also accepts container IDs and ID prefixes in the path, so the client must use the exact container name. The rule would not match an ID, which is the safe direction.
  - The README warns its bind-mount filter "is a request filter, not a sandbox"; never allow container-create endpoints.
- **Relevance:** restarting thrnz or gluetun through the socket breaks the namespace for `network_mode: service:` dependants (F1). Gluetun's control server (F2) rotates in place with no socket access at all.
