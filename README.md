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
dotnet run --project src/Backend --no-launch-profile --urls http://localhost:5000
```

Open <http://localhost:5000>. The backend serves the Angular build and API from the same origin and applies database migrations on startup. Its database defaults to `src/Backend/lookout.db` when started this way. Set `ConnectionStrings__Lookout` to choose another SQLite location and `Runner__BaseUrl` to change the runner address (default `http://localhost:3000`). Set `HEADLESS=true` when starting the runner to hide Chromium.

For UI development, run `npm start -w src/frontend` with the backend listening on port 5000. Angular's development server proxies `/api` to the backend. Rebuild the frontend before running the backend alone or publishing it: `npm run build:frontend && dotnet publish src/Backend -c Release`. The build goes into the backend's `wwwroot` and is included in its publish output.

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

See [the runner documentation](src/runner/README.md) for the Script contract and execution limits. Uploads accept `.ts` and `.js` files up to 64 KiB. Each Run uses a 120-second timeout. Running the same Watch again while it is already running returns HTTP 409.

The first Succeeded Run sets the Baseline silently. Later Succeeded Runs record a Change when their trimmed content differs. Original content is retained, and details never affect comparison. Known failure and Errored Runs stay in history without replacing the Baseline; the list continues to show the most recent successful content.

The initial API is:

| Method | Path | Behavior |
| --- | --- | --- |
| POST | `/api/watches` | Create a Watch atomically with multipart fields `name`, `notificationUrl`, and file `script` |
| GET | `/api/watches` | List Watches with their Baseline, latest successful content, and last Run |
| POST | `/api/watches/{id}/runs` | Execute and record a Run; respond when it completes |
| GET | `/api/watches/{id}/runs` | Read Run history, newest first, including `isChange` |
| GET | `/health` | Check the migrated database |

Run the complete local test suite:

```sh
npm run typecheck
npm run lint
npm test
```

`npm test` runs the runner tests, the .NET tests (including the HTTP client against the real runner), and the Playwright flow. Tests use local pages and temporary SQLite databases. Playwright builds Angular and launches a test-only backend on port 5117 with a fake runner; the production backend always uses the HTTP runner client. `npm run test:runner`, `dotnet test --project tests/Backend.IntegrationTests`, and `npm run test:e2e` run those suites individually. Tests in `examples/` contact an external bot-detection site and are excluded from the automated suite.
