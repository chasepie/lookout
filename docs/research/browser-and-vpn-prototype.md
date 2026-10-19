# Browser and VPN prototype: findings (2026-10-18)

Answers to the three questions in issue #2, from running code. The prototype and its raw evidence (logs and screenshots) are archived in the [`prototype/browser-and-vpn-2026-10-18`](https://github.com/chasepie/lookout/tree/prototype/browser-and-vpn-2026-10-18/prototype/browser-and-vpn) tag and never merged. Links below marked *evidence* point into its `results/` folder. The home IP, ZIP code, store and time zone are masked in all of it.

**Setup:**
- Browserless `ghcr.io/browserless/chromium:v2.57.1` (Chromium 156.0.8078.4), driven by a Playwright 1.64.1 client over `chromium.connect` to `/chromium/playwright`. Launch args are passed in the `launch` query parameter.
- gluetun `qmcgaw/gluetun:v3.41.3` with PIA over OpenVPN and `HTTPPROXY=on`. VPN contexts use `browser.newContext({ proxy: { server: 'http://gluetun:8888' } })`.
- Both ran under Docker Desktop on macOS (arm64), on the home network.
- The gluetun findings were checked against [gluetun's wiki](https://github.com/qdm12/gluetun-wiki) (`888ab89`, which documents the latest code) and the v3.41.3 source. Where they contradicted the first round, the behaviour was retested.

## Answers

| Question | Answer | What Lookout should do |
|---|---|---|
| 1. Does headless Browserless pass bot detection with `--disable-blink-features=AutomationControlled`? | **No.** browserscan says "Robot" because the user agent says `HeadlessChrome`. The flag itself works (`navigator.webdriver` is false). | Stay headless and override `userAgent` on each context, replacing `HeadlessChrome` with `Chrome`. That passes browserscan, and so does the Xvfb fallback. |
| 2. Does the proxy fail closed when the VPN is down? | **Yes, for HTTP(S).** Every proxied request failed while the VPN was stopped, reconnecting or its container was down, and none went out over the home IP. **But WebRTC leaks the home IP** around the proxy. | Have the runner pass `--webrtc-ip-handling-policy=disable_non_proxied_udp` in every connection's launch options. Set `HTTPPROXY_STEALTH=on`. |
| 3a. Does the flag still hold in proxied contexts? | **Yes.** Same verdicts and fingerprints as direct. | Nothing. |
| 3b. Does `PUT /v1/vpn/status` `stopped` then `running` change the exit IP? | **Yes.** 30 of 30 Rotations got a new IP, spread across 8–9 US cities. gluetun doesn't exclude the previous server, though, so a repeat is possible. | Complete a Rotation only on an IP different from the one noted before its first attempt. |
| 3c. How long does a Rotation take? | **Under about 4 s** to a usable proxy when gluetun picks a live server (0.2–4.3 s measured, in 3 s probe steps). But **7 of the 37 server picks were dead**. With its default TCP settings gluetun never leaves a dead server; over UDP, or with OpenVPN flags, it re-picks after 35–75 s. | Treat a Rotation as done only when the exit IP has changed, and make another attempt if it hasn't within about 15 s. Run gluetun with `--connect-retry-max 1 --connect-timeout 10`. Refresh its server list through the control API on first connect, and every ≥ 360 h. |

## 1. Bot detection

Each Browserless mode was tested against [browserscan.net/bot-detection](https://www.browserscan.net/bot-detection) and five retailer homepages (Best Buy, Walmart, Target, Amazon, Nike). Local headful Chromium, which `examples/bot-detection.spec.ts` uses, was the control. Evidence: [`q1-bot.log`](https://github.com/chasepie/lookout/blob/prototype/browser-and-vpn-2026-10-18/prototype/browser-and-vpn/results/q1-bot.log), [`q1-bot-ua.log`](https://github.com/chasepie/lookout/blob/prototype/browser-and-vpn-2026-10-18/prototype/browser-and-vpn/results/q1-bot-ua.log), and one `*-browserscan.jpg`/`.txt` pair per mode.

| Mode | browserscan | `navigator.webdriver` | User agent |
|---|---|---|---|
| Local headful + flag (control) | Normal | false | `Chrome/156` |
| Browserless headless, no flag | **Robot** | true | `HeadlessChrome/156` |
| Browserless headless + flag | **Robot** | false | `HeadlessChrome/156` |
| Browserless headless + flag + context `userAgent` override | Normal | false | `Chrome/156` |
| Browserless headful (Xvfb, `headless: false`) + flag | Normal | false | `Chrome/156` |

- **The user agent alone decides the verdict.** browserscan's full report for headless and headful differs only in the two user-agent lines.
- **Retailers:** none of the 50 homepage loads was blocked, in any mode, direct or proxied, even without the flag. Blocked means status ≥ 400 or a block page ("Access Denied", "Robot or human?", a CAPTCHA). Homepages are a weak test, though. Product pages, and repeated visits on a schedule, were not tried.
- **Best Buy tarpits `HeadlessChrome`.** Its page took 48.7 s to fire `load` in plain headless, but 1.6 s with the user-agent override and 1.7 s headful. `load` is what `page.goto` waits for by default, so a Script would have hit timeouts in plain headless. The other four sites took 0.3–2.8 s in every mode. Evidence: [`q1-load.log`](https://github.com/chasepie/lookout/blob/prototype/browser-and-vpn-2026-10-18/prototype/browser-and-vpn/results/q1-load.log).
- **Memory** was about the same headless and headful: 0.8–1.7 GiB for the Browserless container, measured after each page load with one page open at a time (same log). It doesn't decide between them.
- **Untested reach:** only the page's `navigator.userAgent` was checked. A per-context override may not reach service or shared workers, where `HeadlessChrome` could still show. A browser-wide `--user-agent` launch argument, passed like the WebRTC flag, would cover them too.
- **The user-agent override must track the browser.** Build it from `browser.version()`'s major version, or the user agent and the client hints (`Chromium 156`) will disagree after a Browserless upgrade.
- **Fingerprint gaps browserscan did not flag:** every Browserless mode reports `Linux x86_64`, a SwiftShader WebGL renderer, and time zone `America/Los_Angeles` whatever the exit IP. gluetun's `GET /v1/publicip/ip` returns the exit's `timezone`, so a VPN Run could set the context's `timezoneId` to match. This was not tested.

## 2. Failing closed

Evidence: [`q2-fail-closed.log`](https://github.com/chasepie/lookout/blob/prototype/browser-and-vpn-2026-10-18/prototype/browser-and-vpn/results/q2-fail-closed.log). Each probe requested an IP-echo service over HTTPS, over plain HTTP, and at an IP literal (`https://1.1.1.1/cdn-cgi/trace`, so no DNS is involved).

| State | Proxied requests |
|---|---|
| VPN up | Exit through the VPN IP |
| VPN stopped via the control API (existing context, new context, after 30 s) | All time out (5 s); none reach the home IP |
| VPN restarting via the control API | 0 leaks; usable again 3.6 s after `running` |
| gluetun container stopped | All time out (5 s) |
| gluetun container starting | `ERR_PROXY_CONNECTION_FAILED` until the proxy listens, then 0 leaks; usable 4.2 s after start |

- **Why it fails closed:** gluetun's firewall keeps `OUTPUT DROP` and allows only the chosen VPN server on `eth0` while the tunnel is down. Requests therefore hang until they time out, rather than erroring fast.
- A Run during an outage would hit its timeout and record as Errored. The ticket's fallback, refusing to start VPN Runs unless the rotator reports the VPN connected, is still worth having, but for availability rather than safety.

**WebRTC leaks the home IP around the HTTP proxy.** STUN runs over UDP, which an HTTP proxy doesn't carry, so the browser's own address shows up as a server-reflexive candidate. Evidence: [`q2-webrtc.log`](https://github.com/chasepie/lookout/blob/prototype/browser-and-vpn-2026-10-18/prototype/browser-and-vpn/results/q2-webrtc.log) [`q2-webrtc-repeat.log`](https://github.com/chasepie/lookout/blob/prototype/browser-and-vpn-2026-10-18/prototype/browser-and-vpn/results/q2-webrtc-repeat.log) and the end of [`q2-fail-closed.log`](https://github.com/chasepie/lookout/blob/prototype/browser-and-vpn-2026-10-18/prototype/browser-and-vpn/results/q2-fail-closed.log) (four runs in all), plus browserscan's WebRTC leak test in [`q2-webrtc-*.jpg`](https://github.com/chasepie/lookout/tree/prototype/browser-and-vpn-2026-10-18/prototype/browser-and-vpn/results).

| Variant | Home IP exposed via STUN |
|---|---|
| Context proxy | **Yes** |
| Context proxy + `--force-webrtc-ip-handling-policy=disable_non_proxied_udp` | **Yes** |
| Context proxy + `--webrtc-ip-handling-policy=disable_non_proxied_udp` | No |
| Launch proxy (Browserless `launch.proxy`) | **Yes** |
| Launch proxy + `--force-webrtc-ip-handling-policy=disable_non_proxied_udp` | **Yes** |
| Context proxy + init script deleting `RTCPeerConnection` | No (WebRTC gone) |

- **Use `--webrtc-ip-handling-policy=disable_non_proxied_udp`.** It is a browser-wide launch argument, so it also applies to direct Runs, where it is harmless. browserscan's bot verdict stays Normal with it ([`q3-bot-proxied-webrtc-flag.log`](https://github.com/chasepie/lookout/blob/prototype/browser-and-vpn-2026-10-18/prototype/browser-and-vpn/results/q3-bot-proxied-webrtc-flag.log)).
- The similarly named `--force-…` switch does nothing here.
- Deleting WebRTC also works, but a missing `RTCPeerConnection` is itself a bot signal.

**Plain HTTP reveals the Browserless container's address.** gluetun's HTTP proxy forwards the client's IP by default, so plain-HTTP requests reported `172.21.0.3`. That isn't the home IP, but it shows a proxy is in use. With `HTTPPROXY_STEALTH=on`, they report the VPN IP ([`q2-proxy-stealth.log`](https://github.com/chasepie/lookout/blob/prototype/browser-and-vpn-2026-10-18/prototype/browser-and-vpn/results/q2-proxy-stealth.log)). HTTPS isn't affected.

## 3. Rotation

**The flag holds in proxied contexts.** All four Browserless modes gave the same verdicts and `navigator.webdriver` values through gluetun as they did direct. Retailers localised to the exit: Target showed a Port St Lucie, FL store for a Miami exit ([`q3-bot-proxied.log`](https://github.com/chasepie/lookout/blob/prototype/browser-and-vpn-2026-10-18/prototype/browser-and-vpn/results/q3-bot-proxied.log)).

**A Canadian exit changed what retailers served.** Through Montréal, Best Buy showed its country picker and Nike served Nike CA. Mixing countries in `SERVER_REGIONS` would therefore make a Watch's content change on Rotation, which Lookout would report as a false Change. The regions are now kept to the US (decided 2026-10-18).

**Rotations, with US regions** (East, New York, Florida, California, Chicago, Texas, Washington DC and Atlanta). Each attempt was `PUT stopped` then `PUT running`, and a Rotation took as many attempts as it needed to reach a live server. "Usable" means a request through the proxy succeeded from a fresh browser context. Evidence: [`q3-rotation.log`](https://github.com/chasepie/lookout/blob/prototype/browser-and-vpn-2026-10-18/prototype/browser-and-vpn/results/q3-rotation.log) and [`q3-rotation-udp.log`](https://github.com/chasepie/lookout/blob/prototype/browser-and-vpn-2026-10-18/prototype/browser-and-vpn/results/q3-rotation-udp.log).

| | OpenVPN TCP | OpenVPN UDP |
|---|---|---|
| Rotations | 20 | 10 |
| New exit IP each time | 20 / 20 | 10 / 10 |
| Distinct cities | 8 | 8 |
| Usable, median | 3.7 s | 3.5 s |
| Usable, max | 33 s (three attempts) | 17 s (two attempts) |
| Needed more than one attempt (dead server) | 5 | 1 |

- **Timing:** the probe times out after 3 s, so most medians are "the second attempt worked", and the real time is somewhat shorter. In gluetun's live log (not committed), the tunnel was up about 1 s after `running`, and its own public-IP lookup finished 1–3 s after that.
- **Sameness:** IPs were never repeated, but cities were. gluetun picks a random server from all those matching `SERVER_REGIONS`, so regions with more servers come up more often.
- **TCP and UDP both work.** An early UDP failure turned out to be dead servers too.

**Dead servers are the real problem.**
- gluetun v3.41.3 bundles PIA server data dated 2026-04-22. From the home network, 437 of its 757 US OpenVPN-TCP IPs refused connections, including every IP for US Texas, Atlanta, Silicon Valley and West ([`q3-server-list-probe.txt`](https://github.com/chasepie/lookout/blob/prototype/browser-and-vpn-2026-10-18/prototype/browser-and-vpn/results/q3-server-list-probe.txt)).
- Refreshing the list (`gluetun update -enduser -providers "private internet access"`) still left 216 of 694 IPs (31%) dead. Every region had live servers, though.
- **Refreshing the list, per gluetun's docs and source:**
  - `UPDATER_PERIOD` is off by default. The wiki asks for **no less than 360 h**, because more frequent updates risk PIA blocking the updater.
  - In v3.41.3 the timer starts when the container starts (`internal/updater/loop/loop.go`), so the first periodic update runs a full period later. A fresh install picks from the bundled list until then.
  - The `update` command used above runs outside the VPN. The wiki warns it shows Cloudflare and Google's DNS-over-HTTPS resolvers your public IP, and reserves it for when no built-in server works any more.
  - **`PUT /v1/updater/status` `{"status":"running"}` refreshes the list through the VPN instead.** Retested: it completed in about 60 s, and every server gluetun picked afterwards came from the refreshed list, with no restart ([`q3-updater-api.log`](https://github.com/chasepie/lookout/blob/prototype/browser-and-vpn-2026-10-18/prototype/browser-and-vpn/results/q3-updater-api.log)). After the refresh, 3 of 10 Rotations still needed a second attempt ([`q3-rotation-tcp-flags.log`](https://github.com/chasepie/lookout/blob/prototype/browser-and-vpn-2026-10-18/prototype/browser-and-vpn/results/q3-rotation-tcp-flags.log)).
- **What gluetun does on a dead server depends on the protocol.** The first round claimed it never leaves one. That holds only for its default TCP settings, so I retested both protocols on US Texas, where every bundled IP was dead:
  - **TCP, defaults:** OpenVPN retries the same IP every ~85 s for as long as you let it, at startup too ([`q3-gluetun-stuck-at-startup.log`](https://github.com/chasepie/lookout/blob/prototype/browser-and-vpn-2026-10-18/prototype/browser-and-vpn/results/q3-gluetun-stuck-at-startup.log)). A refused TCP connect is a soft restart against the same remote. gluetun only re-picks when the OpenVPN process exits.
  - **UDP, defaults:** gluetun's OpenVPN config includes `tls-exit`, so the 60 s TLS handshake timeout makes OpenVPN exit. gluetun then picks a new random server after a 15 s backoff, about 75 s per dead server ([`q3-dead-server-udp.log`](https://github.com/chasepie/lookout/blob/prototype/browser-and-vpn-2026-10-18/prototype/browser-and-vpn/results/q3-dead-server-udp.log)).
  - **TCP with `OPENVPN_FLAGS="--connect-retry-max 1 --connect-timeout 10"`:** OpenVPN exits after one failed connect, and gluetun re-picks about every 35 s ([`q3-dead-server-tcp-flags.log`](https://github.com/chasepie/lookout/blob/prototype/browser-and-vpn-2026-10-18/prototype/browser-and-vpn/results/q3-dead-server-tcp-flags.log)). On a fresh install it got past two dead picks at startup and connected in 72 s, with no rotator involved ([`q3-updater-api.log`](https://github.com/chasepie/lookout/blob/prototype/browser-and-vpn-2026-10-18/prototype/browser-and-vpn/results/q3-updater-api.log)). The flags didn't slow live connections: median 3.7 s over 10 Rotations ([`q3-rotation-tcp-flags.log`](https://github.com/chasepie/lookout/blob/prototype/browser-and-vpn-2026-10-18/prototype/browser-and-vpn/results/q3-rotation-tcp-flags.log)).
  - gluetun's healthcheck never helps here. Its startup check runs only after the tunnel is up (`internal/vpn/tunnelup.go`, wiki [faq/healthcheck](https://github.com/qdm12/gluetun-wiki/blob/main/faq/healthcheck.md)).
- `GET /v1/vpn/status` doesn't reveal any of this. It says `running` as soon as the OpenVPN process starts, before any tunnel exists, and flips between `running` and `crashed` while gluetun re-picks. Only `GET /v1/publicip/ip` tells the truth: it returns `""` until the tunnel is up.
- gluetun's own re-pick (35–75 s) is a safety net for when the rotator isn't acting, for example at startup. The rotator's ~15 s attempt is still faster.
- So a Rotation is one or more attempts, and the rotator must:
  - note the exit IP before the first attempt, and keep it for every attempt in that Rotation. Re-noting it per attempt would record `""` after a dead server and let the original exit back in;
  - wait for an exit IP different from it;
  - make another attempt, with VPN Runs still paused, if the IP comes back unchanged or none arrives within about 15 s;
  - at startup, where there is no noted IP, treat any IP as ready, but still make an attempt if none arrives within about 15 s of gluetun starting.
- `/v1/publicip/ip` depends on gluetun's own lookup through the echo services in `PUBLICIP_API`, by default ipinfo, ifconfig.co, ip2location and Cloudflare. A custom `echoip#https://…` URL can replace them. If the lookup fails while the tunnel works, the rotator would keep making attempts, so an IP fetched through the proxy should count too.
- None of the 30 Rotations repeated an IP, but gluetun picks the next server at random from every matching server without excluding the current one (`internal/provider/utils/pick.go`), so a repeat is possible.
- That also requires `GET /v1/publicip/ip` in the control-server role. A role scoped to the three routes worked: other routes, and a wrong key, both got 401.

**Measurement artefact worth knowing about.** A first attempt probed from one long-lived page and measured 13–53 s Rotations ([`q3-rotation-shared-page.log`](https://github.com/chasepie/lookout/blob/prototype/browser-and-vpn-2026-10-18/prototype/browser-and-vpn/results/q3-rotation-shared-page.log)). That page's requests queued behind CONNECTs it had abandoned while the tunnel was down, even after gluetun was serving. A browser context opened before a Rotation can stall like this afterwards. Lookout already waits for in-progress VPN Runs before rotating, and each Run gets a new browser, so Runs shouldn't hit this. Test runs aren't drained, though, so a VPN Test run that spans a Rotation could.

## Other observations

- **Browserless `TIMEOUT` kills sessions.** At 300000 ms (5 min), Browserless closed a session mid-test. It must exceed the longest Watch timeout (10 min).
- **Not covered:** product pages, sustained scheduled traffic, other anti-bot vendors, and DNS leaks. With an HTTP proxy, DNS for proxied requests resolves inside gluetun.
