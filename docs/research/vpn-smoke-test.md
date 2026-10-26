# VPN deployment smoke test (2026-10-26)

A live smoke test of the real deployment, Lookout, runner, Browserless, gluetun and the relay, connected to Private Internet Access, for [#42](https://github.com/chasepie/lookout/issues/42). The automated tests cover the Rotation coordinator against a fake gluetun; this checks it against the real one. The home IP is written `HOME_IP` throughout. No credentials, keys or notification secrets appear here.

**Result:** every VPN check passed. One isolation check failed: pages Browserless loads can reach Lookout through the Docker host's published port, directly and through gluetun's proxy, contradicting `compose.yaml`'s claim. That was [#44](https://github.com/chasepie/lookout/issues/44), decided by accepting it as the same reach any LAN device has, and correcting the docs: [ADR-0006](../adr/0006-browser-pages-reach-lookout-like-a-lan-device.md).

## Setup

| | |
| --- | --- |
| Tested commit | `a5195c9` (`main` as merged into `integrate/spec-1`). The later merges of #40 and #41 change no VPN, runner or compose code. |
| Host | Docker Desktop on macOS (arm64), Docker Engine 29.9.2, linux/arm64 VM, iptables firewall backend, on the home network |
| `gluetun` | `qmcgaw/gluetun:v3.41.3` (`sha256:fa19cc76b2af…6e027`), OpenVPN 2.6.20, PIA over TCP |
| `browserless` | `ghcr.io/browserless/chromium:v2.57.1` (`sha256:93dc614e2532…aac67`), Chrome 156 |
| `gluetun-control` | `alpine/socat:1.8.1.1` (`sha256:7f9a06753033…c3b0`) |
| `lookout`, `runner` | built from the tested commit by `docker compose up -d --build` |
| Configuration | The repository's `compose.yaml` unchanged, with `COMPOSE_PROFILES=vpn`, `LOOKOUT_MAX_CONCURRENT_RUNS=2`, the default memory limits, `LOOKOUT_TIME_ZONE=America/Detroit`, and a set of US `LOOKOUT_VPN_REGIONS`. A disposable project: `docker compose -p lookout-t42` with `LOOKOUT_PORT=18642`, its own volumes, a temporary account and these Watches, all on `https://api.ipify.org/`: |

| Watch | VPN | Schedule | Script |
| --- | --- | --- | --- |
| VPN IP | on | every 3 minutes, no jitter | README's `ip.ts`: the page's text |
| Direct IP | off | daily (Run now only) | `ip.ts` |
| VPN Known failure | on | daily | `ip.ts`, returning `success: false` |
| Direct Known failure | off | daily | the same |
| VPN Slow | on | daily | reads the IP, waits 90 s, reloads, reads it again, and Succeeds only if both match |

Evidence was gathered by a SignalR client on `/api/events` logging every push with a timestamp, a poll of `GET /api/vpn` every 2 s, a headless Playwright browser holding Settings open without reloading, the Run API, and `docker compose logs gluetun`. The [reproduction](#reproduction) section shows how.

**The host slept.** The Mac went into clamshell sleep at 02:59:27 UTC and stayed in Maintenance Sleep, with brief dark wakes, until 04:20:24. Observations in that window aren't counted (see [Host sleep](#host-sleep)). The rest were made with the host awake, under `caffeinate` after 04:21.

## Results

| # | Acceptance criterion | Result |
| --- | --- | --- |
| 1 | VPN Watch's content equals its recorded exit; Direct Watch uses the home exit with no VPN metadata | **Pass** |
| 2 | ≥ 30 minutes: Rotation every 10–15 minutes, exit IP changes, VPN Runs record their exits, Settings updates through SignalR without reload | **Pass** (02:24–02:59, 35 minutes awake) |
| 3 | VPN Known failure rotates early without a retry; 3-minute minimum; Direct Known failure doesn't rotate | **Pass** |
| 4 | Rotation drains a VPN Run; a further VPN Run waits; a VPN Test run is refused; a Direct Run proceeds | **Pass** |
| 5 | Fail closed with the VPN down, Errored within the bounded wait, Direct Runs usable, then recovery | **Pass** |
| 6 | Headful Browserless, WebRTC through the proxy, runner isolation and browser-to-backend isolation | **Fail** on browser-to-backend isolation as documented: [#44](https://github.com/chasepie/lookout/issues/44), accepted in [ADR-0006](../adr/0006-browser-pages-reach-lookout-like-a-lan-device.md), which corrects the docs and adds the check to README "Smoke test". The rest pass. |
| 7 | Server-list refresh observed; real gluetun integration evidenced; unobserved edge cases mapped to tests | **Pass** |
| 8 | Every check has a pass or a concrete failure with reproduction, reported as a linked follow-up | Done: one follow-up, #44, decided in ADR-0006 |
| 9 | Docs reflect what the test revealed; deployment and test data cleaned up | Done |

### 1. VPN and Direct exits

At 02:24:54 the VPN IP Watch's Run Succeeded with content `151.241.119.218`, recording exit `151.241.119.218`, region `New Jersey`: the exit Settings showed. At 02:24:55 the Direct IP Watch's Run Succeeded with content `HOME_IP`, equal to `curl https://api.ipify.org` from the host, with `vpnExitIp` and `vpnRegion` both `null`. Every later Direct Run (Runs 38, 43, 45, 48) did the same.

### 2. Thirty-minute observation

Lookout started at 02:23:07. gluetun's first server was dead (`TCP: connect to 79.127.132.151:8443 failed: Operation timed out`), so at 02:23:24, 15 s after gluetun first answered, Lookout's startup Rotation reconnected it to `151.241.119.218`. Then, untouched:

| Rotation | Started (push) | Ended (push) | Since the last ended | Exit before → after |
| --- | --- | --- | --- | --- |
| startup | 02:23:24 | 02:23:25 | | none → 151.241.119.218 (New Jersey) |
| scheduled | 02:37:58.450 | 02:37:59.603 | 14 min 33 s | 151.241.119.218 → 151.240.205.35 (New York) |
| scheduled | 02:51:37.790 | 02:51:38.948 | 13 min 38 s | 151.240.205.35 → 37.19.198.115 (New York) |

Every one of the VPN IP Watch's 12 Runs in the window (Runs 1 and 4–14, from 02:24:54 to 02:58:38, every 3 minutes) Succeeded, and each one's content equals the exit it records: five through `151.241.119.218`, four through `151.240.205.35`, and three through `37.19.198.115`. Each new exit appears from the first Run after its Rotation.

The Settings page, opened once at 02:28:29 and never reloaded (`page load #1` only), showed each Rotation as SignalR pushed it, 0.15 s after the push:

```
02:37:58.602 [loads=1] Status Rotating Region New Jersey Exit IP 151.241.119.218 Switching to a new exit IP. VPN Runs wait until it's done.
02:37:59.615 [loads=1] Status Connected Region New York Exit IP 151.240.205.35
02:51:37.938 [loads=1] Status Rotating Region New York Exit IP 151.240.205.35 Switching to a new exit IP. VPN Runs wait until it's done.
02:51:38.955 [loads=1] Status Connected Region New York Exit IP 37.19.198.115
```

Drops and reconnections between Rotations were pushed within the 30-second check, too: gluetun stopped at 04:32:53 and was pushed as not connected at 04:33:04; it reconnected at 04:35:43 and was pushed at 04:35:54.

### 3. Early Rotation after a Known failure

| Time | Event |
| --- | --- |
| 02:25:16 | Direct Known failure Run, content `HOME_IP`. No Rotation followed: the next began at 02:37:58, 14 min 33 s after the previous one ended, on the schedule. An early one would have come at 02:26:25, three minutes after the last. |
| 04:23:08–04:23:10 | VPN Known failure Run 32, through `191.96.106.202`. |
| 04:23:10.151 | Rotation starts, at once, as the last ended more than 3 minutes before. gluetun's first pick, `209.87.162.60`, was dead; Lookout's second attempt, 17 s later, reached `72.9.243.67`. |
| 04:23:28.682 | Rotation ends, exit `72.9.243.67` (Georgia). |
| 04:23:28–04:23:31 | VPN Known failure Run 33, through `72.9.243.67`, requests another. |
| 04:26:28.687 | Rotation starts: 3 min 0.005 s after the last ended, not before. |
| 04:26:29.845 | Rotation ends, exit `212.56.54.154` (New Jersey). |

The Watch's history has exactly one Run for each Run now (32, 33 and later 37), so no Known failure was retried.

### 4. Draining, waiting, refusing and carrying on

| Time | Event |
| --- | --- |
| 04:29:46.8 | VPN Slow Run 39 starts, through `212.56.54.154`. |
| 04:29:51–52 | VPN Known failure Run 37 requests an early Rotation. |
| 04:29:52.565 | Rotation starts (pushed as Rotating); it waits for Run 39. |
| 04:29:52.6 | VPN IP Run 40 is asked for, and waits. |
| 04:29:53 | VPN Test run asked for: refused at once (`durationMs: 0`) with "The VPN is rotating to a new exit IP, so this Test run was not started. Try again in a minute." |
| 04:29:53.7–04:29:54.2 | Direct IP Run 38 Succeeds with `HOME_IP` during the Rotation. |
| 04:31:17.666 | Run 39 Succeeds with `212.56.54.154 212.56.54.154`: its exit didn't change under it. |
| 04:31:17 | Only now does gluetun log `[vpn] stopping` for the Rotation's first attempt. Two dead servers (`193.56.117.243`, `72.9.243.52`) cost an attempt each. |
| 04:31:53.403 | Rotation ends, exit `158.173.25.72` (New York). |
| 04:31:55.7 | Run 40, having waited, Succeeds through `158.173.25.72` (its Script took 2.3 s). |

### 5. Failing closed

**Tunnel stopped inside a running gluetun** (`PUT /v1/vpn/status` `stopped` at 04:36:04):

- Lookout reported `connected: false`, "gluetun's VPN is stopped." within 3 s.
- `curl -x http://gluetun:8888 https://api.ipify.org` from the Browserless container got no response (HTTP 000, held until curl's 15 s limit), twice. Nothing went out through the home IP.
- VPN IP Run 49, asked for at 04:36:23, waited and was recorded Errored at 04:38:23, exactly 2 minutes later, without running its Script: "The VPN is not connected, so this VPN Run was not started. gluetun's VPN is stopped."
- A VPN Test run at 04:36:25 was refused at once: "The VPN is not connected, so this Test run was not started. gluetun's VPN is stopped."
- Direct IP Run 48 Succeeded with `HOME_IP` at 04:36:25.
- After `running` at 04:38:23, Lookout saw `151.240.205.41` (New York) at 04:38:25, and VPN IP Run 50 Succeeded through it.

**gluetun's container stopped** (`docker compose stop gluetun` at 04:32:53, README's smoke step 5):

- Lookout reported "gluetun's control server did not respond within 10 seconds."
- The proxy didn't answer (HTTP 000).
- VPN IP Run 46, asked for at 04:33:03, was Errored at 04:35:03, 2 minutes later: "The VPN is not connected, so this VPN Run was not started. gluetun's control server did not respond within 10 seconds."
- Direct IP Run 45 Succeeded with `HOME_IP` at 04:33:05.
- After `docker compose start gluetun`, it connected at 04:35:43 (`72.9.243.172`, Georgia), and VPN IP Run 47 Succeeded through it.

Separately, a key that isn't Lookout's got HTTP 401 from gluetun's control server.

### 6. Browser and isolation

**Headful Browserless and WebRTC.** A Test run through the VPN and one direct each reported, from the page:

| | VPN | Direct |
| --- | --- | --- |
| Page's IP | `151.241.119.218` (the VPN's exit) | `HOME_IP` |
| User agent | `Mozilla/5.0 (X11; Linux x86_64) … Chrome/156.0.0.0 Safari/537.36`, with no `HeadlessChrome` | the same |
| `navigator.webdriver` | `false` | `false` |
| ICE candidates gathered in 8 s against `stun:stun.l.google.com:19302` | none | none |

No candidate at all, not even an mDNS host candidate, means WebRTC offered no UDP route around the proxy. During a VPN Run, Browserless's Chromium processes had `--disable-blink-features=AutomationControlled`, `--webrtc-ip-handling-policy=disable_non_proxied_udp` and `--ozone-platform=x11`, no `--headless`, and Xvfb was running.

**Runner isolation** (README smoke steps 5, 6 and 8): the runner's `fetch('https://example.com')` failed with `EAI_AGAIN`; it has no mounts (`[]`); it's only on `browsers` and `runs`; requests from Browserless without the token, or with a wrong one, got HTTP 401; and `docker compose top runner` listed only `docker-init` and `node index.ts`.

**Browser-to-backend isolation: fails** as `compose.yaml` then described it ([#44](https://github.com/chasepie/lookout/issues/44)). This reach is now accepted, as pages get only what any LAN device gets ([ADR-0006](../adr/0006-browser-pages-reach-lookout-like-a-lan-device.md)), and README "Smoke test" checks it.

| From | Target | Result |
| --- | --- | --- |
| `gluetun` container (README smoke step 7) | Lookout's three addresses | unreachable, as documented |
| Browserless container | `lookout` by name, gluetun's relay | unreachable |
| Direct Test run, in Chromium | `http://lookout:8080/health` | `ERR_NAME_NOT_RESOLVED` |
| Direct Test run | Lookout's address on its `lan` network, port 8080 | HTTP 200 (`/api/session`) |
| Direct Test run | the `internet` network's gateway, published port | HTTP 200 `Healthy` |
| Direct Test run | `host.docker.internal`, published port | HTTP 200 (`/api/session`) |
| VPN Test run, through gluetun's proxy | the `internet` network's gateway, published port | HTTP 200 `Healthy` |
| VPN Test run | Lookout's container addresses, `host.docker.internal` | timed out |

gluetun's firewall lets its proxy reach the subnets of its own interfaces, which include the `internet` network's gateway: the Docker host, where Lookout's port is published. Reaching Lookout's `lan` address from the `internet` network may be peculiar to Docker Desktop. A page that reaches Lookout gets only what needs no login (`/health`, `GET` and `POST /api/session`, `POST /api/setup`), as it has no Lookout cookie, which is what any LAN device gets. Also, the Browserless container can reach gluetun's control server directly on `gluetun:8000` (HTTP 401 without the key), as a page through the proxy already could; compose's comments account for the latter.

### 7. Server list and gluetun

- gluetun started by writing its bundled list ("creating /gluetun/servers.json with 20957 hardcoded servers").
- At 02:23:38, 14 s after the VPN first connected, Lookout ran gluetun's updater (`PUT /v1/updater/status`). gluetun logged "updating Private Internet Access servers..." through the VPN, Lookout checked it every 5 s, and it completed at 02:24:09 (31 s). Lookout recorded `VpnServerListRefreshedAt = 2026-10-26 02:23:38` in its database, and `servers.json` on the `gluetun-data` volume was rewritten.
- When gluetun's container restarted at 04:35, it kept the refreshed list from the volume, and Lookout made no second refresh request, as one had completed within 360 hours.
- The refreshed list still had dead servers: 3 of the 9 servers gluetun picked in Lookout's Rotations while the host was awake timed out, as did its very first pick, from the bundled list. Each cost one 15–17 s attempt before Lookout made another, and no Rotation needed more than 3 attempts or came near the 2-minute limit.

Exits seen, all in the US: `151.241.119.218` (New Jersey), `151.240.205.35` and `37.19.198.115` (New York), `212.32.69.228` (Texas), `191.96.106.202` (California), `72.9.243.67` and `72.9.243.172` (Georgia), `212.56.54.154` (New Jersey), `158.173.25.72` and `151.240.205.41` (New York).

**Not seen live, and covered by automated tests instead:**

| Edge case | Test |
| --- | --- |
| gluetun reconnecting to the identical exit | `VpnRotationTests.ARotationMakesAnotherAttemptUntilTheExitIpChanges` |
| A Rotation giving up after 2 minutes without a new exit | `VpnRotationTests.ARotationGivesUpWhenTheVpnDoesNotReconnect` |
| A Rotation waiting no longer than the 10-minute maximum Run timeout | `VpnRotationTests.ARotationWaitsNoLongerThanTheLongestRunTimeoutForVpnRuns` |
| gluetun having no exit IP of its own while its tunnel is up | `GluetunVpnRotatorTests.CountsAnExitIpFetchedThroughTheProxyWhenGluetunHasHadNoneForTenSeconds` |
| A server-list refresh that fails, retried at the next start | `VpnRotationTests.ARefreshThatFailsIsTriedAgainAtTheNextStart` |
| A Rotation waiting for a refresh in progress | `VpnRotationTests.ARotationWaitsForTheServerListRefreshInProgress` |
| gluetun refusing Lookout's key | `GluetunVpnRotatorTests.IsMisconfiguredWhenGluetunRefusesTheApiKey` |

### Host sleep

While the Mac slept (02:59:27–04:20:24), Docker Desktop's VM ran only during dark wakes. OpenVPN's keepalive timed out (`Inactivity timeout (--ping-restart)`) and reconnected over and over. Lookout still reported the VPN connected, as gluetun keeps its exit IP through a soft restart, so 14 scheduled VPN Runs went ahead and were Errored with `net::ERR_TUNNEL_CONNECTION_FAILED`. None went out through the home IP. Timers didn't count the sleep, so a scheduled Rotation came 44 minutes after the one before (at 03:35:37), and gluetun reconnected to a new exit on its own at 03:59:51. Within a minute of the host waking, the VPN was healthy and Runs Succeeded again. A homelab host that never sleeps won't see this, and none of it counts toward the results above.

## Reproduction

1. Copy `example.env` to `.env` beside `compose.yaml`, and fill in the VPN settings as README "VPN" describes.
2. Start a disposable project on a free port: `LOOKOUT_PORT=18642 docker compose -p lookout-smoke up -d --build`.
3. Open `http://localhost:18642`, set up a temporary account, and create the Watches in [Setup](#setup).
4. Follow README "Smoke test" with the VPN. For checks 3 and 4:
   - Run now on *VPN Known failure*, and watch Settings: it shows Rotating at once (if the last Rotation ended more than 3 minutes before). Run it again as soon as the Rotation ends: the next comes 3 minutes after.
   - More than 3 minutes later, Run now on *VPN Slow*, then within a few seconds on *VPN Known failure*. While Settings shows Rotating, Run now on *VPN IP* (it waits), upload a Script to *VPN Known failure* (the Test run is refused at once), and Run now on *Direct IP* (it Succeeds). *VPN Slow* Succeeds with the same IP twice, and gluetun logs `[vpn] stopping` only after it ends.
   - To stop the tunnel but not gluetun, send `PUT /v1/vpn/status` with `{"status":"stopped"}` and Lookout's key in `X-API-Key` to `http://gluetun:8000` from a container on the `vpn-proxy` network, such as `docker compose exec browserless curl …`, and `{"status":"running"}` afterwards. If you read the key from `.env` in a script, strip any quotes around it: compose does.
5. For WebRTC, upload as a Test run a Script that creates an `RTCPeerConnection` with a STUN server and a data channel, makes an offer, and returns the ICE candidates it gathers after a few seconds. For browser-to-backend isolation, follow README "Smoke test" step 10, directly and through the VPN, and the reproduction in [#44](https://github.com/chasepie/lookout/issues/44).
6. To log pushes as they happen, connect a `@microsoft/signalr` client to `/api/events` with the session's cookies and the `X-XSRF-TOKEN` header.
7. Keep the host awake throughout (`caffeinate -ims` on macOS).
8. Tear down: `docker compose -p lookout-smoke down -v --rmi local`.

## Cleanup

`docker compose -p lookout-t42 down -v --rmi local --remove-orphans` removed the project's five containers, seven networks, both volumes (`lookout-t42_lookout-data`, `lookout-t42_gluetun-data`, so the temporary account, Watches and Runs) and its two built images. The scratch files (Scripts, logs, cookie jar, a database copy) and the copied `.env` were deleted afterwards. No other compose project or volume was touched.
