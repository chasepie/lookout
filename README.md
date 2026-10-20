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

Open <http://localhost:5280>. The backend serves the Angular build and API from the same origin and applies database migrations on startup. Its database defaults to `src/Backend/lookout.db` when started this way. A database the Development environment creates starts with one example Watch, which runs `examples/bot-detection.ts` against browserscan.net every hour; other environments start empty. Set `ConnectionStrings__Lookout` to choose another SQLite location and `Runner__BaseUrl` to change the runner address (default `http://localhost:3000`). Schedules use the host's time zone; set `Scheduling__TimeZone` (such as `Europe/London`) to choose another. `Scheduling__MaxConcurrentRuns` limits how many Runs execute at once (default 2). Set `HEADLESS=true` when starting the runner to hide Chromium.

When an HTTPS endpoint is configured (including the `https` launch profile), HTTP requests redirect to it. The HTTP-only command above continues to serve HTTP. A runner base URL may include a path prefix, such as `http://proxy/runner/`.

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

See [the runner documentation](src/runner/README.md) for the Script contract and execution limits. Uploads accept `.ts` and `.js` files up to 64 KiB. Each Run uses a 120-second timeout. Running the same Watch again while it is already running returns HTTP 409. Once started, a Run continues and is recorded even if its browser tab disconnects; application shutdown still cancels active Runs.

The first Succeeded Run sets the Baseline silently. Later Succeeded Runs record a Change when their trimmed content differs. Original content is retained, and details never affect comparison. Known failure and Errored Runs stay in history without replacing the Baseline; the list continues to show the most recent successful content.

On first launch, Lookout asks you to set up its single local account; after that, it asks you to log in. The login is kept in an `HttpOnly`, `SameSite=Strict` cookie and lasts until you log out. Logging out ends that session on the server, so a copied cookie stops working too. Every endpoint below except the first three needs a logged-in session and returns HTTP 401 without one; endpoints added later are protected unless they explicitly allow anonymous access. Every POST, PUT, PATCH and DELETE must also send the `X-XSRF-TOKEN` header with the value of the `Lookout.XSRF-TOKEN` cookie that `GET /api/session` sets, or it gets HTTP 400; this stops pages from other services on the same host acting with your login. The token belongs to one login, so fetch `GET /api/session` again after setup, login or logout. The Angular app does all of this automatically. Every response also carries a Content Security Policy that allows only Lookout's own scripts, enforces Trusted Types and stops other sites framing the app.

The initial API is:

| Method | Path | Behavior |
| --- | --- | --- |
| GET | `/api/session` | Report whether the account exists (`accountExists`) and who is logged in (`userName`, or `null`), and set the `Lookout.XSRF-TOKEN` request-token cookie |
| POST | `/api/setup` | Create the account from JSON `userName` and `password` (8+ characters) and log in; HTTP 409 once the account exists |
| POST | `/api/session` | Log in with JSON `userName` and `password`; HTTP 401 for wrong credentials |
| DELETE | `/api/session` | Log out, ending the session |
| POST | `/api/watches` | Create a Watch atomically with multipart fields `name`, `notificationUrl`, file `script`, and a schedule: `scheduleKind` (`interval` with `every` and `unit` of `minutes`, `hours` or `days`; `daily` with `timeOfDay`; or `cron` with a five-field `cron`) and optional `jitterMinutes` (0–60). Schedules that run more than once every 3 minutes are rejected |
| GET | `/api/watches` | List Watches with their Baseline (the latest successful content), last Run, schedule and `nextRunAt` |
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
