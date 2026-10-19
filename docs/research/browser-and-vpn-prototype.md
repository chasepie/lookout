# Browser and VPN prototype: findings (2026-10-18)

Answers to the three questions in issue #2, from running code. The prototype and its raw evidence (logs and screenshots) are on the [`prototype/browser-and-vpn`](https://github.com/chasepie/lookout/tree/prototype/browser-and-vpn/prototype/browser-and-vpn) branch, which is never merged. Links below marked *evidence* point into its `results/` folder. The home IP, ZIP code, store and time zone are masked in all of it.

**Setup:**
- Browserless `ghcr.io/browserless/chromium:v2.57.1` (Chromium 156.0.8078.4), driven by a Playwright 1.64.1 client over `chromium.connect` to `/chromium/playwright`. Launch args are passed in the `launch` query parameter.
- gluetun `qmcgaw/gluetun:v3.41.3` with PIA over OpenVPN and `HTTPPROXY=on`. VPN contexts use `browser.newContext({ proxy: { server: 'http://gluetun:8888' } })`.
- Both ran under Docker Desktop on macOS (arm64), on the home network.

## Answers

| Question | Answer | What Lookout should do |
|---|---|---|
| 1. Does headless Browserless pass bot detection with `--disable-blink-features=AutomationControlled`? | **No.** browserscan says "Robot" because the user agent says `HeadlessChrome`. The flag itself works (`navigator.webdriver` is false). | Stay headless and override `userAgent` on each context, replacing `HeadlessChrome` with `Chrome`. That passes, and so does the Xvfb fallback. |
| 2. Does the proxy fail closed when the VPN is down? | **Yes, for HTTP(S).** Every proxied request failed while the VPN was stopped, reconnecting or its container was down, and none went out over the home IP. **But WebRTC leaks the home IP** around the proxy. | Launch Browserless with `--webrtc-ip-handling-policy=disable_non_proxied_udp`. Set `HTTPPROXY_STEALTH=on`. |
| 3a. Does the flag still hold in proxied contexts? | **Yes.** Same verdicts and fingerprints as direct. | Nothing. |
| 3b. Does `PUT /v1/vpn/status` `stopped` then `running` change the exit IP? | **Yes.** 30 of 30 Rotations got a new IP, spread across 8–9 US cities. | Nothing. |
| 3c. How long does a Rotation take? | **About 3.5–4.3 s** to a usable proxy when gluetun picks a live server. But **7 of the 37 server picks were dead**, and gluetun never leaves a dead server on its own. | Treat a Rotation as done only when `GET /v1/publicip/ip` returns an IP, and rotate again if it hasn't within about 15 s. Refresh gluetun's server list regularly. |

## 1. Bot detection

Each Browserless mode was tested against [browserscan.net/bot-detection](https://www.browserscan.net/bot-detection) and five retailer homepages (Best Buy, Walmart, Target, Amazon, Nike). Local headful Chromium, which `examples/bot-detection.spec.ts` uses, was the control. Evidence: [`q1-bot.log`](https://github.com/chasepie/lookout/blob/prototype/browser-and-vpn/prototype/browser-and-vpn/results/q1-bot.log), [`q1-bot-ua.log`](https://github.com/chasepie/lookout/blob/prototype/browser-and-vpn/prototype/browser-and-vpn/results/q1-bot-ua.log), and one `*-browserscan.jpg`/`.txt` pair per mode.

| Mode | browserscan | `navigator.webdriver` | User agent |
|---|---|---|---|
| Local headful + flag (control) | Normal | false | `Chrome/156` |
| Browserless headless, no flag | **Robot** | true | `HeadlessChrome/156` |
| Browserless headless + flag | **Robot** | false | `HeadlessChrome/156` |
| Browserless headless + flag + context `userAgent` override | Normal | false | `Chrome/156` |
| Browserless headful (Xvfb, `headless: false`) + flag | Normal | false | `Chrome/156` |

- **The user agent alone decides the verdict.** browserscan's full report for headless and headful differs only in the two user-agent lines.
- **Retailers:** none of the 50 homepage loads was blocked, in any mode, direct or proxied, even without the flag. Blocked means status ≥ 400 or a block page ("Access Denied", "Robot or human?", a CAPTCHA). Homepages are a weak test, though. Product pages, and repeated visits on a schedule, were not tried.
- **Best Buy tarpits `HeadlessChrome`.** Its page took 48.7 s to fire `load` in plain headless, but 1.6 s with the user-agent override and 1.7 s headful. `load` is what `page.goto` waits for by default, so a Script would have hit timeouts in plain headless. The other four sites took 0.3–2.8 s in every mode. Evidence: [`q1-load.log`](https://github.com/chasepie/lookout/blob/prototype/browser-and-vpn/prototype/browser-and-vpn/results/q1-load.log).
- **Memory** was about the same headless and headful: 0.8–1.7 GiB for the Browserless container, measured after each page load with one page open at a time (same log). It doesn't decide between them.
- **The user-agent override must track the browser.** Build it from `browser.version()`'s major version, or the user agent and the client hints (`Chromium 156`) will disagree after a Browserless upgrade.
- **Fingerprint gaps browserscan did not flag:** every Browserless mode reports `Linux x86_64`, a SwiftShader WebGL renderer, and time zone `America/Los_Angeles` whatever the exit IP. gluetun's `GET /v1/publicip/ip` returns the exit's `timezone`, so a VPN Run could set the context's `timezoneId` to match. This was not tested.

## 2. Failing closed

Evidence: [`q2-fail-closed.log`](https://github.com/chasepie/lookout/blob/prototype/browser-and-vpn/prototype/browser-and-vpn/results/q2-fail-closed.log). Each probe requested an IP-echo service over HTTPS, over plain HTTP, and at an IP literal (`https://1.1.1.1/cdn-cgi/trace`, so no DNS is involved).

| State | Proxied requests |
|---|---|
| VPN up | Exit through the VPN IP |
| VPN stopped via the control API (existing context, new context, after 30 s) | All time out (5 s); none reach the home IP |
| VPN restarting via the control API | 0 leaks; usable again 3.6 s after `running` |
| gluetun container stopped | All time out (5 s) |
| gluetun container starting | `ERR_PROXY_CONNECTION_FAILED` until the proxy listens, then 0 leaks; usable 4.2 s after start |

- **Why it fails closed:** gluetun's firewall keeps `OUTPUT DROP` and allows only the chosen VPN server on `eth0` while the tunnel is down. Requests therefore hang until they time out, rather than erroring fast.
- A Run during an outage would hit its timeout and record as Errored. The ticket's fallback, refusing to start VPN Runs unless the rotator reports the VPN connected, is still worth having, but for availability rather than safety.

**WebRTC leaks the home IP around the HTTP proxy.** STUN runs over UDP, which an HTTP proxy doesn't carry, so the browser's own address shows up as a server-reflexive candidate. Evidence: [`q2-webrtc.log`](https://github.com/chasepie/lookout/blob/prototype/browser-and-vpn/prototype/browser-and-vpn/results/q2-webrtc.log) and [`q2-webrtc-repeat.log`](https://github.com/chasepie/lookout/blob/prototype/browser-and-vpn/prototype/browser-and-vpn/results/q2-webrtc-repeat.log) (four runs in all), plus browserscan's WebRTC leak test in [`q2-webrtc-*.jpg`](https://github.com/chasepie/lookout/tree/prototype/browser-and-vpn/prototype/browser-and-vpn/results).

| Variant | Home IP exposed via STUN |
|---|---|
| Context proxy | **Yes** |
| Context proxy + `--force-webrtc-ip-handling-policy=disable_non_proxied_udp` | **Yes** |
| Context proxy + `--webrtc-ip-handling-policy=disable_non_proxied_udp` | No |
| Launch proxy (Browserless `launch.proxy`) | **Yes** |
| Launch proxy + `--force-webrtc-ip-handling-policy=disable_non_proxied_udp` | **Yes** |
| Context proxy + init script deleting `RTCPeerConnection` | No (WebRTC gone) |

- **Use `--webrtc-ip-handling-policy=disable_non_proxied_udp`.** It is a browser-wide launch argument, so it also applies to direct Runs, where it is harmless. browserscan's bot verdict stays Normal with it ([`q3-bot-proxied-webrtc-flag.log`](https://github.com/chasepie/lookout/blob/prototype/browser-and-vpn/prototype/browser-and-vpn/results/q3-bot-proxied-webrtc-flag.log)).
- The similarly named `--force-…` switch does nothing here.
- Deleting WebRTC also works, but a missing `RTCPeerConnection` is itself a bot signal.

**Plain HTTP reveals the Browserless container's address.** gluetun's HTTP proxy forwards the client's IP by default, so plain-HTTP requests reported `172.21.0.3`. That isn't the home IP, but it shows a proxy is in use. With `HTTPPROXY_STEALTH=on`, they report the VPN IP ([`q2-proxy-stealth.log`](https://github.com/chasepie/lookout/blob/prototype/browser-and-vpn/prototype/browser-and-vpn/results/q2-proxy-stealth.log)). HTTPS isn't affected.

## 3. Rotation

**The flag holds in proxied contexts.** All four Browserless modes gave the same verdicts and `navigator.webdriver` values through gluetun as they did direct. Retailers localised to the exit: Target showed a Port St Lucie, FL store for a Miami exit ([`q3-bot-proxied.log`](https://github.com/chasepie/lookout/blob/prototype/browser-and-vpn/prototype/browser-and-vpn/results/q3-bot-proxied.log)).

**A Canadian exit changed what retailers served.** Through Montréal, Best Buy showed its country picker and Nike served Nike CA. Mixing countries in `SERVER_REGIONS` would therefore make a Watch's content change on Rotation, which Lookout would report as a false Change. The regions are now kept to the US (decided 2026-10-18).

**Rotations, with US regions** (East, New York, Florida, California, Chicago, Texas, Washington DC and Atlanta). Each Rotation was `PUT stopped` then `PUT running`. "Usable" means a request through the proxy succeeded from a fresh browser context. Evidence: [`q3-rotation.log`](https://github.com/chasepie/lookout/blob/prototype/browser-and-vpn/prototype/browser-and-vpn/results/q3-rotation.log) and [`q3-rotation-udp.log`](https://github.com/chasepie/lookout/blob/prototype/browser-and-vpn/prototype/browser-and-vpn/results/q3-rotation-udp.log).

| | OpenVPN TCP | OpenVPN UDP |
|---|---|---|
| Rotations | 20 | 10 |
| New exit IP each time | 20 / 20 | 10 / 10 |
| Distinct cities | 8 | 8 |
| Usable, median | 3.7 s | 3.5 s |
| Usable, max | 33 s (two retries) | 17 s (one retry) |
| Needed another Rotation (dead server) | 5 | 1 |

- **Timing:** the probe times out after 3 s, so most medians are "the second attempt worked", and the real time is somewhat shorter. In gluetun's live log (not committed), the tunnel was up about 1 s after `running`, and its own public-IP lookup finished 1–3 s after that.
- **Sameness:** IPs were never repeated, but cities were. gluetun picks a random server from all those matching `SERVER_REGIONS`, so regions with more servers come up more often.
- **TCP and UDP both work.** An early UDP failure turned out to be dead servers too.

**Dead servers are the real problem.**
- gluetun v3.41.3 bundles PIA server data dated 2026-04-22. From the home network, 437 of its 757 US OpenVPN-TCP IPs refused connections, including every IP for US Texas, Atlanta, Silicon Valley and West ([`q3-server-list-probe.txt`](https://github.com/chasepie/lookout/blob/prototype/browser-and-vpn/prototype/browser-and-vpn/results/q3-server-list-probe.txt)).
- Refreshing the list (`gluetun update -enduser -providers "private internet access"`) still left 216 of 694 IPs (31%) dead. Every region had live servers, though. Only this manual refresh was tested. gluetun's periodic refresh (`UPDATER_PERIOD`) was configured, but no run lasted long enough to trigger it.
- When gluetun picks a dead server, OpenVPN retries that same IP every ~85 s for as long as you let it. gluetun's "restart VPN on healthcheck failure" never fires, because the tunnel never came up. The same happens at container start ([`q3-gluetun-stuck-at-startup.log`](https://github.com/chasepie/lookout/blob/prototype/browser-and-vpn/prototype/browser-and-vpn/results/q3-gluetun-stuck-at-startup.log)).
- Throughout, `GET /v1/vpn/status` says `running`. Only `GET /v1/publicip/ip` tells the truth: it returns `""` until the tunnel is up.
- So the rotator must:
  - wait for a non-empty public IP;
  - rotate again if none arrives within about 15 s;
  - do the same at startup.
- That also requires `GET /v1/publicip/ip` in the control-server role. A role scoped to the three routes worked: other routes, and a wrong key, both got 401.

**Measurement artefact worth knowing about.** A first attempt probed from one long-lived page and measured 13–53 s Rotations ([`q3-rotation-shared-page.log`](https://github.com/chasepie/lookout/blob/prototype/browser-and-vpn/prototype/browser-and-vpn/results/q3-rotation-shared-page.log)). That page's requests queued behind CONNECTs it had abandoned while the tunnel was down, even after gluetun was serving. A browser context opened before a Rotation can stall like this afterwards. Lookout already waits for in-progress VPN Runs before rotating, and each Run gets a new browser, so it shouldn't hit this.

## Other observations

- **Browserless `TIMEOUT` kills sessions.** At 300000 ms (5 min), Browserless closed a session mid-test. It must exceed the longest Watch timeout (10 min).
- **Not covered:** product pages, sustained scheduled traffic, other anti-bot vendors, and DNS leaks. With an HTTP proxy, DNS for proxied requests resolves inside gluetun.
