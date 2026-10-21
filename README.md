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

Open <http://localhost:5280>. The backend serves the Angular build and API from the same origin and applies database migrations on startup. Its database is `lookout.db` in the backend's content root, `src/Backend`, when started this way. Set `Storage__DataDirectory` to keep it in another directory, or `ConnectionStrings__Lookout` to choose another SQLite connection string, whose relative path is resolved against that directory (a `file:` URI is used as given). A database the Development environment creates starts with one example Watch, which runs `examples/bot-detection.ts` against browserscan.net every hour; other environments start empty. Set `Runner__BaseUrl` to change the runner address (default `http://localhost:3000`). Schedules use the host's time zone; set `Scheduling__TimeZone` (such as `Europe/London`) to choose another. `Scheduling__MaxConcurrentRuns` limits how many Runs execute at once (default 2). Set `HEADLESS=true` when starting the runner to hide Chromium.

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

See [the runner documentation](src/runner/README.md) for the Script contract and execution limits. Uploads accept `.ts` and `.js` files up to 64 KiB. Each Run uses its Watch's timeout: 120 seconds by default, editable from 1 to 600. Running the same Watch again while it is already running returns HTTP 409. Once started, a Run continues and is recorded even if its browser tab disconnects; application shutdown still cancels active Runs.

The first Succeeded Run sets the Baseline silently. Later Succeeded Runs record a Change when their trimmed content differs. Original content is retained, and details never affect comparison. Known failure and Errored Runs stay in history without replacing the Baseline; the list continues to show the most recent successful content.

On first launch, Lookout asks you to set up its single local account; after that, it asks you to log in. The login is kept in an `HttpOnly`, `SameSite=Strict` cookie and lasts until you log out. Logging out ends that session on the server, so a copied cookie stops working too. Every endpoint below except the first three needs a logged-in session and returns HTTP 401 without one; endpoints added later are protected unless they explicitly allow anonymous access. Every POST, PUT, PATCH and DELETE must also send the `X-XSRF-TOKEN` header with the value of the `Lookout.XSRF-TOKEN` cookie that `GET /api/session` sets, or it gets HTTP 400; this stops pages from other services on the same host acting with your login. The token belongs to one login, so fetch `GET /api/session` again after setup, login or logout. The Angular app does all of this automatically. Every response also carries a Content Security Policy that allows only Lookout's own scripts, enforces Trusted Types and stops other sites framing the app.

Each Watch reports a status. Paused means the user stopped its scheduled Runs; Run now still works. Otherwise Failing means it is in a streak of Known failure or Errored Runs, which the next Succeeded Run ends. This applies even before its first Succeeded Run. Pending means it has no Baseline yet, and Healthy that its latest Run Succeeded. Pausing keeps the Baseline and clears the next Run; resuming schedules the next Run from now and keeps the failure streak, so a Watch that was Failing stays Failing until a Run Succeeds. A Run that was in progress when the Watch was deleted is not recorded. Deleting a Watch deletes its Script versions and Run history.

The initial API is:

| Method | Path | Behavior |
| --- | --- | --- |
| GET | `/api/session` | Report whether the account exists (`accountExists`) and who is logged in (`userName`, or `null`), and set the `Lookout.XSRF-TOKEN` request-token cookie |
| POST | `/api/setup` | Create the account from JSON `userName` and `password` (8+ characters) and log in; HTTP 409 once the account exists |
| POST | `/api/session` | Log in with JSON `userName` and `password`; HTTP 401 for wrong credentials |
| DELETE | `/api/session` | Log out, ending the session |
| POST | `/api/watches` | Create a Watch atomically with multipart fields `name`, `notificationUrl`, file `script`, and a schedule: `scheduleKind` (`interval` with `every` and `unit` of `minutes`, `hours` or `days`; `daily` with `timeOfDay`; or `cron` with a five-field `cron`) and optional `jitterMinutes` (0–60). Schedules that run more than once every 3 minutes are rejected |
| GET | `/api/watches` | List Watches with their Baseline (the latest successful content), status, failure streak, timeout, last Run, schedule and `nextRunAt` |
| GET | `/api/watches/{id}` | Read one Watch, in the same shape as the list |
| PUT | `/api/watches/{id}` | Edit a Watch's settings with JSON `{ name, notificationUrl, timeoutSeconds }` |
| DELETE | `/api/watches/{id}` | Delete a Watch with its Script versions and Run history |
| POST | `/api/watches/{id}/pause` | Pause a Watch's scheduled Runs, keeping its Baseline and clearing its next Run |
| POST | `/api/watches/{id}/resume` | Resume a paused Watch, scheduling its next Run from now |
| PUT | `/api/watches/{id}/schedule` | Change a Watch's schedule and jitter, with the same fields as a JSON body, and schedule its next Run from now |
| POST | `/api/watches/{id}/runs` | Execute and record a Run; respond when it completes |
| GET | `/api/watches/{id}/runs` | Read Run history, newest first, including `isChange`; defaults to 50 Runs, with `limit=1..100` and `before=<Run ID>` for older pages |
| GET | `/health` | Check the migrated database |

Run the complete local test suite:

```sh
npm run typecheck
npm run lint
npm test
```

`npm test` runs the runner tests, the .NET tests (including the HTTP client against the real runner), and the Playwright flow. Tests use local pages and temporary SQLite databases. The end-to-end command builds Angular and the test host before Playwright launches a test-only backend on port 5117 with a fake runner; the production backend always uses the HTTP runner client. `npm run test:runner`, `dotnet test --project tests/Backend.IntegrationTests`, and `npm run test:e2e` run those suites individually. Tests in `examples/` contact an external bot-detection site and are excluded from the automated suite.

## Deploying

Lookout deploys to a homelab with one Docker Compose file, [`compose.yaml`](compose.yaml), which runs three services:

- **`lookout`:** the backend, serving the Angular app on port 8080. Its database is on the `lookout-data` volume, mounted at `/data`, the image's data directory. Nothing else mounts it.
- **`runner`:** runs Scripts. It's attached only to internal networks with no route to the internet, so a Script's own Node code can't reach it, and it has nothing mounted: no database, secrets or backend configuration ([ADR-0003](docs/adr/0003-scripts-run-in-an-offline-runner-container.md)). Its container limits a Script's memory outside V8's heap.
- **`browserless`:** runs the headless Chromium each Script drives. It shares an internal network with the runner, and has one with internet access: it's the only part of a Run that reaches the internet.

Each pair of services that talk shares a network of its own, so Browserless's Chromium, which loads whatever pages Scripts visit, can't reach Lookout. It can reach the runner, which refuses requests from web pages.

On a host with Docker Compose, from a clone of this repository:

```sh
docker compose up -d --build
```

Then open `http://<host>:8080` and set up the account. To upgrade, pull the latest code and run the same command again. To back up, copy the `lookout-data` volume. Settings go in the environment or a `.env` file beside `compose.yaml`:

| Variable | Default | Meaning |
| --- | --- | --- |
| `LOOKOUT_PORT` | `8080` | The host port Lookout is served on |
| `LOOKOUT_TIME_ZONE` | UTC | The time zone schedules use, such as `Europe/London` |
| `LOOKOUT_MAX_CONCURRENT_RUNS` | `2` | How many Runs execute at once, and how many browsers Browserless runs at once. Raise the two memory limits with it |
| `LOOKOUT_RUNNER_MEMORY` | `1536m` | The runner's memory limit: 640 MB per concurrent Run, plus 256 MB for the runner itself |
| `LOOKOUT_BROWSERLESS_MEMORY` | `3g` | Browserless's memory limit: about 1.5 GB per concurrent Run |

Lookout serves plain HTTP and is meant to be reached only from your local network ([ADR-0002](docs/adr/0002-designed-for-lan-only-homelab-deployment.md)). For HTTPS, put your own reverse proxy in front of it, sending `X-Forwarded-Proto`, as Caddy, nginx and Traefik setups usually do. Lookout then treats requests that came over HTTPS as such, and marks its cookies `Secure` for them. Read ADR-0002 before exposing it to the internet: Lookout isn't hardened for that, and the ADR lists what it would take.

Browserless's `TIMEOUT` is set to 11 minutes, above the longest Run timeout, since its default of 30 seconds would end longer Runs. The runner's `playwright` must match Browserless's Playwright at the major.minor level: `ghcr.io/browserless/chromium:v2.57.1` serves Playwright 1.60 to 1.64, and the runner has 1.64. Upgrade them together.

**Licence:** Lookout is MIT-licensed. Browserless, which it runs as a separate container from Browserless's own image, is licensed under the SSPL or a commercial licence, and is free for non-commercial use. Check [Browserless's terms](https://github.com/browserless/browserless) before using Lookout commercially.

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
