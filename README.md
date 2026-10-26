# Lookout

Watch webpages with your own TypeScript or JavaScript Scripts. Create a Watch, choose **Run now**, and see its latest content. Lookout compares successful results, records Changes, and keeps Run history in SQLite.

Requires .NET 10 (see `global.json`) and Node 24 (see `.nvmrc`).

```sh
npm ci
npx playwright install chromium
npm run build:frontend
```

Start the runner and backend in separate terminals:

```sh
npm start -w src/runner
```

```sh
dotnet run --project src/Backend --no-launch-profile --urls http://localhost:5280
```

Open <http://localhost:5280>. The backend serves the Angular build and API from the same origin and applies database migrations on startup. Its database is `lookout.db` in the backend's content root, `src/Backend`, when started this way. Set `Storage__DataDirectory` to keep it in another directory, or `ConnectionStrings__Lookout` to choose another SQLite connection string, whose relative path is resolved against that directory (a `file:` URI is used as given). A database the Development environment creates starts with one example Watch, which runs `examples/bot-detection.ts` against browserscan.net every hour; other environments start empty. Set `Runner__BaseUrl` to change the runner address (default `http://127.0.0.1:3000`), and `Runner__Token` to the token the runner was started with as `RUNNER_TOKEN`, if any. A runner on another machine requires one, so Lookout won't start with a `Runner__BaseUrl` that isn't on this machine and no `Runner__Token`. Schedules use the time zone chosen in Settings. A database without one starts with `Scheduling__TimeZone` (such as `Europe/London`) if set, or else the host's time zone, and keeps it from then on (see [Time zone](#time-zone)). `Scheduling__MaxConcurrentRuns` sets how many Run slots there are: how many Runs and Test runs execute at once (default 2). Set `HEADLESS=true` when starting the runner to hide Chromium. The VPN is disabled unless `Vpn__ApiKey` is set; see [VPN](#vpn).

When an HTTPS endpoint is configured (including the `https` launch profile), HTTP requests redirect to it, except those to `/health`, so health probes can check over HTTP. The HTTP-only command above continues to serve HTTP. A runner base URL may include a path prefix, such as `http://proxy/runner/`.

For UI development, run `npm start -w src/frontend` with the backend started from its `https` launch profile (`dotnet run --project src/Backend`), which listens on `https://localhost:5281`. Angular's development server proxies `/api` to that endpoint and accepts the ASP.NET Core development certificate; run `dotnet dev-certs https` once if you don't have one. Rebuild the frontend before running the backend alone or publishing it: `npm run build:frontend && dotnet publish src/Backend -c Release`. The build goes into the backend's `wwwroot` and is included in its publish output.

A Script default-exports an object or class with a `run` method. For example, save this as `price.ts` and upload it when creating a Watch:

```ts
import type { LookoutScript } from '@chasepie/lookout-scripting';

export default {
  async run(page, context) {
    await page.goto(context.notificationUrl);
    return { success: true, content: await page.locator('.price').innerText() };
  },
} satisfies LookoutScript;
```

To type-check Scripts in your own project, install `@chasepie/lookout-scripting`, `playwright` and `@types/node` as dev dependencies, as Playwright's types use Node's. The scripting package is published to GitHub Packages, which requires a login even for public packages. Add `@chasepie:registry=https://npm.pkg.github.com` to your project's `.npmrc`, then run `npm login --scope=@chasepie --auth-type=legacy --registry=https://npm.pkg.github.com` with your GitHub user name and a personal access token (classic) with the `read:packages` scope as the password. `@chasepie/lookout-scripting@edge` has the types on `main` (see [Releasing](#releasing)).

See [the runner documentation](src/runner/README.md) for the Script contract and execution limits. Uploads accept `.ts` and `.js` files up to 64 KiB. Each Run uses its Watch's timeout: 120 seconds by default, editable from 1 to 600. Running the same Watch again while it is already running returns HTTP 409. Once started, a Run continues and is recorded even if its browser tab disconnects; application shutdown still cancels active Runs.

The first Succeeded Run sets the Baseline silently. Later Succeeded Runs record a Change when their trimmed content differs. Original content is retained, and details never affect comparison. Known failure and Errored Runs stay in history without replacing the Baseline; the list continues to show the most recent successful content.

To revise a Watch's Script, upload a new Script version from its page. Choosing a file starts a Test run: the Script runs once with the Watch's Notification URL and timeout, and the page shows its outcome, content, console output and a screenshot. A Test run is never recorded and never touches the Watch's history, Baseline, status or notifications. Saving the file makes it the Watch's current Script version. By default this resets the Baseline and failure streak, so the Watch goes back to Pending and its next Succeeded Run sets a new Baseline silently. Tick "Keep Baseline" if the new version reports its content the same way. Every Run records the Script version it ran, and each version's source stays viewable. A Run of the old version still in progress when a new one is saved is not recorded.

On first launch, Lookout asks you to set up its single local account; after that, it asks you to log in. The login is kept in an `HttpOnly`, `SameSite=Strict` cookie and lasts until you log out. Logging out ends that session on the server, so a copied cookie stops working too. Every endpoint below except the first three needs a logged-in session and returns HTTP 401 without one; endpoints added later are protected unless they explicitly allow anonymous access. Every POST, PUT, PATCH and DELETE must also send the `X-XSRF-TOKEN` header with the value of the `Lookout.XSRF-TOKEN` cookie that `GET /api/session` sets, or it gets HTTP 400; this stops pages from other services on the same host acting with your login. The token belongs to one login, so fetch `GET /api/session` again after setup, login or logout. The Angular app does all of this automatically. Every response also carries a Content Security Policy that allows only Lookout's own scripts, enforces Trusted Types and stops other sites framing the app.

Each Watch reports a status. Paused means the user stopped its scheduled Runs; Run now still works. Otherwise Failing means it is in a streak of Known failure or Errored Runs, which the next Succeeded Run ends. This applies even before its first Succeeded Run. Pending means it has no Baseline yet, and Healthy that its latest Run Succeeded. Pausing keeps the Baseline and clears the next Run; resuming schedules the next Run from now and keeps the failure streak, so a Watch that was Failing stays Failing until a Run Succeeds. A Run that was in progress when the Watch was deleted is not recorded. Deleting a Watch deletes its Script versions and Run history.

Each Watch notifies the Notification channels it chooses, configured once in **Settings**. A Change sends its old and new content. A failure streak sends one notification in total: on its first Errored Run or its third Known failure, whichever comes first. When a streak that notified ends with a Succeeded Run, a recovered notification follows, and then a Change notification if the content differs from the Baseline. The Succeeded Run that sets a Baseline sends nothing, even after a streak that notified, as there is nothing to compare it with. A Notification channel that fails to send doesn't fail the Run or stop the others.

Each Watch chooses whether its Runs go through the VPN. A VPN Run's browser uses gluetun's HTTP proxy, and the Run records the exit IP it went out through and the region that IP is in, such as `Florida`, as gluetun's IP lookup reports it. That's not necessarily the name of the PIA region gluetun picked. A Direct Run uses a plain browser on the same Browserless, from your own IP. A VPN Run is never sent from your own IP instead. If the VPN isn't connected, the Run waits up to 2 minutes for it, as gluetun takes a minute or two to connect after it starts, and other Runs can go ahead meanwhile. If it still isn't connected, or no VPN is configured, or gluetun refuses Lookout's key, the Run is refused: it's recorded as Errored without running its Script, saying why the VPN isn't connected. Without a VPN, everything else works as before.

The initial API is:

| Method | Path                                            | Behavior                                                                                                                                                                                                                                                                                                                                                                                                             |
| ------ | ----------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| GET    | `/api/session`                                  | Report whether the account exists (`accountExists`) and who is logged in (`userName`, or `null`), and set the `Lookout.XSRF-TOKEN` request-token cookie                                                                                                                                                                                                                                                              |
| POST   | `/api/setup`                                    | Create the account from JSON `userName` and `password` (8+ characters) and log in; HTTP 409 once the account exists                                                                                                                                                                                                                                                                                                  |
| POST   | `/api/session`                                  | Log in with JSON `userName` and `password`; HTTP 401 for wrong credentials                                                                                                                                                                                                                                                                                                                                           |
| DELETE | `/api/session`                                  | Log out, ending the session                                                                                                                                                                                                                                                                                                                                                                                          |
| POST   | `/api/watches`                                  | Create a Watch atomically with multipart fields `name`, `notificationUrl`, file `script`, optional `usesVpn` (`true` or `false`, the default), and a schedule: `scheduleKind` (`interval` with `every` and `unit` of `minutes`, `hours` or `days`; `daily` with `timeOfDay`; or `cron` with a five-field `cron`) and optional `jitterMinutes` (0–60). Schedules that run more than once every 3 minutes are rejected |
| GET    | `/api/watches`                                  | List Watches with their Baseline (the latest successful content), status, failure streak, `usesVpn`, timeout, last Run, schedule and `nextRunAt`                                                                                                                                                                                                                                                                     |
| GET    | `/api/watches/{id}`                             | Read one Watch, in the same shape as the list                                                                                                                                                                                                                                                                                                                                                                        |
| PUT    | `/api/watches/{id}`                             | Edit a Watch's settings with JSON `{ name, notificationUrl, timeoutSeconds, usesVpn }`, all required                                                                                                                                                                                                                                                                                                                 |
| DELETE | `/api/watches/{id}`                             | Delete a Watch with its Script versions and Run history                                                                                                                                                                                                                                                                                                                                                              |
| POST   | `/api/watches/{id}/pause`                       | Pause a Watch's scheduled Runs, keeping its Baseline and clearing its next Run                                                                                                                                                                                                                                                                                                                                       |
| POST   | `/api/watches/{id}/resume`                      | Resume a paused Watch, scheduling its next Run from now                                                                                                                                                                                                                                                                                                                                                              |
| PUT    | `/api/watches/{id}/schedule`                    | Change a Watch's schedule and jitter, with the same fields as a JSON body, and schedule its next Run from now                                                                                                                                                                                                                                                                                                        |
| POST   | `/api/watches/{id}/runs`                        | Execute and record a Run; respond when it completes                                                                                                                                                                                                                                                                                                                                                                  |
| GET    | `/api/watches/{id}/runs`                        | Read Run history, newest first, including `isChange` and `scriptVersionId`, and `vpnRegion` and `vpnExitIp` for VPN Runs; defaults to 50 Runs, with `limit=1..100` and `before=<Run ID>` for older pages                                                                                                                                                                                                             |
| POST   | `/api/watches/{id}/script-versions`             | Save multipart file `script` as the Watch's current Script version, resetting its Baseline and failure streak unless `keepBaseline` is `true`                                                                                                                                                                                                                                                                        |
| GET    | `/api/watches/{id}/script-versions`             | List the Watch's Script versions, newest first, with `number`, `fileKind`, `uploadedAt` and `isCurrent`                                                                                                                                                                                                                                                                                                              |
| GET    | `/api/watches/{id}/script-versions/{versionId}` | Read one Script version, including its `source`                                                                                                                                                                                                                                                                                                                                                                      |
| POST   | `/api/watches/{id}/test-runs`                   | Execute multipart file `script` once as a Test run, with the Watch's Notification URL and timeout, and return its outcome, content, details, error message, duration, console output and screenshot without recording anything. A VPN Watch's Test run goes through the VPN, and is refused at once if the VPN isn't connected                                                                                       |
| GET    | `/api/vpn`                                      | Report the VPN's status: whether one is configured (`enabled`), `connected`, its `region` and `exitIp`, and an `error` if gluetun couldn't be read                                                                                                                                                                                                                                                                   |
| GET    | `/health`                                       | Check the migrated database                                                                                                                                                                                                                                                                                                                                                                                          |

Run the complete local test suite:

```sh
npm run typecheck
npm run lint
npm test
```

`npm test` runs the runner tests, the .NET tests (including the HTTP client against the real runner), and the Playwright flow. Tests use local pages and temporary SQLite databases. The end-to-end command builds Angular and the test host before Playwright launches a test-only backend on port 5117 with a fake runner; the production backend always uses the HTTP runner client. `npm run test:runner`, `dotnet test --project tests/Backend.IntegrationTests`, and `npm run test:e2e` run those suites individually. Tests in `examples/` contact an external bot-detection site and are excluded from the automated suite.

## Releasing

Once every check passes, CI calls CD ([`cd.yml`](.github/workflows/cd.yml)), which publishes the images and the scripting package from each push to `main`, as edge builds, and from each release:

|                                                                                       | Push to `main`                                                                                                                         | Release `v0.2.0`            |
| ------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------- | --------------------------- |
| `ghcr.io/chasepie/lookout` and `ghcr.io/chasepie/lookout-runner`, for amd64 and arm64 | `edge` and `sha-<short SHA>`                                                                                                           | `0.2.0`, `0.2` and `latest` |
| `@chasepie/lookout-scripting` on GitHub Packages                                      | `0.2.1-edge.<run number>`, tagged `edge`, when what it would publish differs from the last edge, or the last edge came before `v0.2.0` | `0.2.0`, tagged `latest`    |

To release, run `gh release create v0.2.0 --generate-notes`. It tags the latest commit on `main` and writes the release notes from the titles of the pull requests merged since the last release. The tag sets every published version, so no `package.json` is bumped for a release. Lookout and the runner always share a version, as they must match, and neither is tagged until every build of both has succeeded. Each platform builds on a GitHub runner of its own architecture, without emulation. CD refuses a release tag that isn't a version, such as `v0.2.0`, isn't on `main`, or is older than the newest release, which would move `latest` back. Before the first release, edges are numbered from the root `package.json`'s version, such as `0.0.1-edge.<run number>`. A pre-release is a version with a suffix, such as `gh release create v0.2.0-rc.1 --prerelease --generate-notes`: `--prerelease` alone only marks the GitHub release, and CD would still publish it as `latest`. A pre-release publishes images tagged only `0.2.0-rc.1` and the package tagged `next`, leaving `latest` as it was. Versions stay below 1.0 while Lookout is in beta, so any release may need changes to a deployment or to Scripts: add those steps to its release notes.

## Deploying

Lookout deploys to a homelab with one Docker Compose file, [`compose.yaml`](compose.yaml), which runs three services, and two more for the [VPN](#vpn):

- **`lookout`:** the backend, serving the Angular app on port 8080. Its database is on the `lookout-data` volume, mounted at `/data`, the image's data directory. Nothing else mounts it.
- **`runner`:** runs Scripts. It's attached only to internal networks with no route to the internet, so a Script's own Node code can't reach it, and it has nothing mounted: no database, secrets or backend configuration ([ADR-0003](docs/adr/0003-scripts-run-in-an-offline-runner-container.md)). Its container limits a Script's memory outside V8's heap.
- **`browserless`:** runs the headful Chromium each Script drives, on a virtual display. It shares an internal network with the runner, and has one with internet access: it's the only part of a Run that reaches the internet.
- **`gluetun`:** optional, in the `vpn` profile. The VPN that VPN Runs go through, as an HTTP proxy that Browserless reaches over an internal network of their own. Its proxy forwards to any network gluetun is on, so gluetun shares none with Lookout. Instead, **`gluetun-control`**, a relay, carries Lookout's requests to gluetun's control server, and a page loaded through the proxy can follow it only back to that server, which needs Lookout's key.

Each pair of services that talk shares a network of its own, so Browserless's Chromium, which loads whatever pages Scripts visit, shares no network with Lookout. It can still reach Lookout through the Docker host, at the port Lookout is published on, like any device on your LAN, and so can a page loaded through gluetun's proxy. Such a page gets only what needs no login: `/health`, the session check, login, and first-launch setup ([ADR-0006](docs/adr/0006-browser-pages-reach-lookout-like-a-lan-device.md)). So, as for any LAN device, use a strong password, as login isn't rate-limited, and set up the account as soon as Lookout first starts. It can reach the runner, which refuses requests without the runner token, which only Lookout has, and requests from web pages.

On a host with Docker Compose, from a clone of this repository, copy [`example.env`](example.env) to `.env`, set `LOOKOUT_RUNNER_TOKEN` in it to a random value, such as from `openssl rand -hex 32`, and start Lookout:

```sh
docker compose up -d --build
```

Then open `http://<host>:8080` and set up the account. To upgrade, pull the latest code and run the same command again. If your `.env` predates `LOOKOUT_RUNNER_TOKEN`, set it first: without it, Lookout and the runner stop at startup, as `docker compose logs` shows. To back up, copy the `lookout-data` volume. Settings go in the environment or the `.env` file beside `compose.yaml`. `example.env` lists them all with their defaults:

| Variable                      | Default          | Meaning                                                                                                                                                                          |
| ----------------------------- | ---------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `LOOKOUT_RUNNER_TOKEN`        | required         | The token Lookout sends the runner with every Script, without which the runner refuses it: letters, digits, `-` and `_` only. Without it, Lookout and the runner stop at startup |
| `LOOKOUT_PORT`                | `8080`           | The host port Lookout is served on                                                                                                                                               |
| `LOOKOUT_TIME_ZONE`           | UTC              | The time zone a new install starts with, such as `Europe/London`; see [Time zone](#time-zone)                                                                                    |
| `LOOKOUT_MAX_CONCURRENT_RUNS` | `2`              | How many Runs execute at once, and how many browsers Browserless runs at once. Raise the two memory limits with it                                                               |
| `LOOKOUT_RUNNER_MEMORY`       | `1536m`          | The runner's memory limit: 640 MB per concurrent Run, plus 256 MB for the runner itself                                                                                          |
| `LOOKOUT_BROWSERLESS_MEMORY`  | `3g`             | Browserless's memory limit: about 1.5 GB per concurrent Run                                                                                                                      |
| `COMPOSE_PROFILES`            | unset            | `vpn` runs gluetun and its relay                                                                                                                                                 |
| `LOOKOUT_VPN_API_KEY`         | unset            | The key Lookout uses for gluetun's control server: letters, digits, `-` and `_` only. Without it, the VPN is disabled                                                            |
| `PIA_USER`, `PIA_PASSWORD`    | unset            | Your Private Internet Access login                                                                                                                                               |
| `LOOKOUT_VPN_REGIONS`         | eight US regions | The PIA regions gluetun picks a server from, comma-separated                                                                                                                     |

Lookout serves plain HTTP and is meant to be reached only from your local network ([ADR-0002](docs/adr/0002-designed-for-lan-only-homelab-deployment.md)). For HTTPS, put your own reverse proxy in front of it, sending `X-Forwarded-Proto`, as Caddy, nginx and Traefik setups usually do. Lookout then treats requests that came over HTTPS as such, and marks its cookies `Secure` for them. The proxy must also pass WebSocket upgrades through to `/api/events`, which pushes Runs and status changes to the app as they happen. Caddy and Traefik do this by default; nginx needs the `Upgrade` and `Connection` headers forwarded. Read ADR-0002 before exposing it to the internet: Lookout isn't hardened for that, and the ADR lists what it would take.

Browserless's `TIMEOUT` is set to 11 minutes, above the longest Run timeout, since its default of 30 seconds would end longer Runs. The runner's `playwright` must match Browserless's Playwright at the major.minor level: `ghcr.io/browserless/chromium:v2.57.1` serves Playwright 1.60 to 1.64, and the runner has 1.64. Upgrade them together.

**Licence:** Lookout is MIT-licensed. Browserless, which it runs as a separate container from Browserless's own image, is licensed under the SSPL or a commercial licence, and is free for non-commercial use. Check [Browserless's terms](https://github.com/browserless/browserless) before using Lookout commercially.

### Time zone

Daily and cron schedules run at their times in Lookout's single, install-wide time zone; interval schedules don't depend on it. Settings shows it beside the schedule inputs, and lets you change it to any IANA time zone, such as `Europe/London`.

- **Initial configuration:** the first time Lookout starts without a saved time zone, whether a new install or one upgraded from a version that didn't save it, it saves `LOOKOUT_TIME_ZONE` (`Scheduling__TimeZone` outside Docker Compose). If that's unset, it saves the host's time zone, which is UTC in the Docker image. Nothing else in Settings changes.
- **Persistence:** the saved time zone is kept in the database on the `lookout-data` volume, so it survives restarts, upgrades and recreating the containers, and is backed up with the volume.
- **Precedence:** once saved, the time zone in Settings wins. Changing `LOOKOUT_TIME_ZONE` afterwards has no effect; change the time zone in Settings instead. An invalid `LOOKOUT_TIME_ZONE` still stops Lookout at startup.

Saving a new time zone takes effect at once, without a restart. Each daily and cron Watch's next Run is scheduled afresh from now in the new time zone, with its Jitter, and at least 3 minutes after its last scheduled Run. Lookout doesn't catch up on a Run the change puts in the past. Interval Watches keep their next Run, Paused Watches stay unscheduled until resumed in the new time zone, and Runs already executing finish and are recorded as usual.

### VPN

VPN Runs go through [gluetun](https://github.com/qdm12/gluetun) connected to [Private Internet Access](https://www.privateinternetaccess.com/) (PIA) over OpenVPN ([ADR-0004](docs/adr/0004-gluetun-for-vpn-rotation.md)). Lookout works fully without it: leave it out, and Watches that use the VPN have their Runs refused.

To set it up:

1. Find your PIA login for OpenVPN: the username (`p` followed by digits) and password you sign in to PIA's apps with.
2. Generate a key for Lookout to use with gluetun's control server. It may contain only letters, digits, `-` and `_`, and Lookout refuses to start with any other key:

   ```sh
   openssl rand -hex 32
   ```

3. Add these to the `.env` file beside `compose.yaml`:

   ```sh
   COMPOSE_PROFILES=vpn
   LOOKOUT_VPN_API_KEY=<the key>
   PIA_USER=<your PIA username>
   PIA_PASSWORD=<your PIA password>
   ```

4. Run `docker compose up -d`. The host needs `/dev/net/tun`, which gluetun opens with the `NET_ADMIN` capability.
5. Open **Settings** in Lookout. Once gluetun has connected, which can take a minute or two, the VPN shows as Connected, with its region and exit IP.

Each time gluetun connects, it picks a server at random from the regions in `LOOKOUT_VPN_REGIONS`, by default `US East`, `US New York`, `US Florida`, `US California`, `US Chicago`, `US Texas`, `US Washington DC` and `US Atlanta`. Any of PIA's regions will do, but keep them in one country: an exit in another changes what retailers serve, which shows up as false Changes. Region names are gluetun's, such as `US Seattle`; see its [PIA server list](https://github.com/qdm12/gluetun/blob/v3.41.3/internal/storage/servers.json).

Lookout rotates the VPN to a new exit IP every 10 to 15 minutes, so a site a Watch reads often doesn't block one IP. A Known failure on a VPN Run brings the next Rotation forward, though never to within 3 minutes of the last one; the Run isn't retried, and the Watch's next Run uses the new IP. A Rotation:

1. stops VPN Runs from starting. Those that come due wait, and start once it's done.
2. waits for VPN Runs in progress to finish, for up to 10 minutes, the longest Run timeout.
3. asks gluetun to reconnect, which picks another server from `LOOKOUT_VPN_REGIONS`.
4. waits until gluetun reports an exit IP different from the one before. gluetun can pick the same exit again, or a dead server, so after 15 seconds without a new IP it asks again, and gives up after 2 minutes.

Direct Runs carry on throughout. A VPN Watch's Test run asked for during a Rotation is refused at once, rather than kept waiting. **Settings** shows the VPN as Rotating while one is under way, and its new exit IP afterwards, without reloading. Between Rotations, Lookout checks gluetun every 30 seconds, so Settings also shows within 30 seconds when the VPN drops or reconnects.

If a Rotation gives up, VPN Runs start again: through the old exit if gluetun reconnected to it, until the next Rotation, and otherwise each waits up to 2 minutes and is Errored. Settings shows the VPN as not connected, with gluetun's reason. Lookout sends no separate notification that the VPN is down; each VPN Watch notifies through its own failure streak. A Rotation counts any new exit IP, even one in the same subnet or region as the last, which consecutive Rotations often are, so a site that blocks a whole subnet or network may keep blocking a Watch after an early Rotation.

gluetun is pinned to v3.41.3, the version the prototype tested, and later than v3.40, which made its control server require authentication. Lookout's key opens only the routes it uses. With an HTTP proxy, WebRTC could reveal your own IP, so every browser is launched with `--webrtc-ip-handling-policy=disable_non_proxied_udp`, and `HTTPPROXY_STEALTH` keeps gluetun from passing Browserless's address on. Requests through the proxy fail rather than leak while the tunnel is down.

The list of PIA servers bundled with gluetun's image goes stale, and more of its servers are dead, so connecting takes longer: each dead server costs about 35 seconds before gluetun picks another. So the first time Lookout sees the VPN connected after it starts, it has gluetun refresh the list through the VPN, into the `gluetun-data` volume, unless it already did in the last 360 hours. Rotations wait for the refresh, which takes about a minute, and one that doesn't complete is tried again when Lookout next starts. gluetun also refreshes it by itself every 360 hours after it starts. Refreshing more often risks PIA blocking the updater.

If the VPN hasn't connected 15 seconds after Lookout first reaches gluetun's control server, Lookout rotates it, as gluetun may be sitting on a dead server.

Outside Docker, the backend reads the same settings:
- `Vpn__ApiKey`.
- `Vpn__ControlUrl`, by default `http://gluetun-control:8000`, the relay.
- `Vpn__ProxyUrl`, by default `http://gluetun:8888`, the address Browserless reaches gluetun's proxy at.
- `Vpn__ProxyUsername` and `Vpn__ProxyPassword`, if gluetun's proxy has `HTTPPROXY_USER` and `HTTPPROXY_PASSWORD` set. They can't be written into `Vpn__ProxyUrl`, as Chromium wouldn't use them.

### Smoke test

The automated tests don't cover the real Browserless stack, so check a deployment by hand:

1. `docker compose ps` shows all three services running, and `lookout` and `browserless` as healthy.
2. Open Lookout and set up the account.
3. Create a Watch with the Notification URL `https://example.com/` and this Script, saved as `title.ts`:

   ```ts
   import type { LookoutScript } from '@chasepie/lookout-scripting';

   export default {
     async run(page, context) {
       await page.goto(context.notificationUrl);
       return { success: true, content: await page.title() };
     },
   } satisfies LookoutScript;
   ```

4. Choose **Run now**. The Run Succeeds with the content `Example Domain`.
5. Check that the runner is offline. This prints `fetch failed: EAI_AGAIN`, or another network error:

   ```sh
   docker compose exec runner node -e "fetch('https://example.com').then(() => console.log('reached'), (e) => console.log('fetch failed:', e.cause?.code))"
   ```

6. Check that the runner has nothing mounted. This prints `[]`:

   ```sh
   docker inspect "$(docker compose ps -q runner)" --format '{{json .Mounts}}'
   ```

7. Check that a Script can run past Browserless's default timeout. Add `await page.waitForTimeout(45_000);` after `page.goto` in the Script, create a Watch with it and choose **Run now**. The Run Succeeds after about 45 seconds.
8. Check that killed Scripts don't linger. Change the Script's wait to `await new Promise(() => {});`, so it never finishes, and choose **Run now** a few times. Each Run is Errored once its timeout passes. Afterwards, `docker compose top runner` lists only `docker-init` and `node index.ts`, with no `<defunct>` (zombie) process.
9. Check that Lookout can reach your Notification channels. In **Settings**, add a Discord Notification channel with a webhook URL (or a Pushover Notification channel), and choose **Send test**. The test notification arrives.
10. Check what pages can reach of Lookout through the Docker host ([ADR-0006](docs/adr/0006-browser-pages-reach-lookout-like-a-lan-device.md)). Find the host's gateway on Browserless's `internet` network with `docker network inspect lookout_internet --format '{{(index .IPAM.Config 0).Gateway}}'`, and upload this Script to a Watch, substituting it and your `LOOKOUT_PORT`, to start a Test run:

    ```ts
    import type { LookoutScript } from '@chasepie/lookout-scripting';

    export default {
      async run(page) {
        const health = await page.goto('http://<gateway>:<LOOKOUT_PORT>/health', { timeout: 8000 });
        const healthBody = await page.locator('body').innerText();
        const watches = await page.goto('http://<gateway>:<LOOKOUT_PORT>/api/watches', { timeout: 8000 });
        return { success: true, content: `${health?.status()} ${healthBody}, ${watches?.status()}` };
      },
    } satisfies LookoutScript;
    ```

    The Test run Succeeds with `200 Healthy, 401`: the page reaches Lookout like any LAN device, but gets nothing that needs a login.

With the VPN set up, also check that VPN Runs leave from the VPN's exit and direct Runs from your own IP:

1. Settings shows the VPN as Connected, with a region and exit IP.
2. Create a Watch with the Notification URL `https://api.ipify.org/` and this Script, saved as `ip.ts`, and turn on **Send Runs through the VPN**:

   ```ts
   import type { LookoutScript } from '@chasepie/lookout-scripting';

   export default {
     async run(page, context) {
       await page.goto(context.notificationUrl);
       return { success: true, content: await page.locator('body').innerText() };
     },
   } satisfies LookoutScript;
   ```

3. Choose **Run now**. The Run's content is the exit IP it records in its history, and it differs from your own IP (`curl https://api.ipify.org` from the host).
4. Turn off **Send Runs through the VPN**, save, and choose **Run now** again. The Run's content is your own IP, and it records no VPN exit.
5. Stop gluetun (`docker compose stop gluetun`) and choose **Run now** with the VPN turned back on. After 2 minutes, the Run is Errored, saying the VPN is not connected. Start gluetun again afterwards.
6. Check that the VPN rotates. With gluetun running again, change the `ip.ts` Watch's schedule to every 3 minutes, and leave **Settings** open. Within 15 minutes, the exit IP it shows changes without reloading, and the VPN shows as Rotating for the few seconds in between. After half an hour, the Watch's Run history shows several exit IPs, and every Run Succeeded, its content matching the exit IP it records.
7. Check that pages can't reach Lookout through gluetun's proxy, which connects from inside gluetun. This prints `unreachable` for each of Lookout's addresses:

   ```sh
   for ip in $(docker inspect "$(docker compose ps -q lookout)" --format '{{range .NetworkSettings.Networks}}{{.IPAddress}} {{end}}'); do
     docker compose exec gluetun wget -q -O /dev/null -T 5 -t 1 "http://$ip:8080/health" && echo "reached $ip" || echo "unreachable $ip"
   done
   ```

   Pages can still reach Lookout's published port through the Docker host, as any LAN device can ([ADR-0006](docs/adr/0006-browser-pages-reach-lookout-like-a-lan-device.md)). Repeat step 10 above on a Watch with **Send Runs through the VPN** turned on: the Test run, through gluetun's proxy, also Succeeds with `200 Healthy, 401`.

[The VPN smoke test report](docs/research/vpn-smoke-test.md) records a full run of these checks against PIA, including early Rotation, draining and fail-closed behavior.
