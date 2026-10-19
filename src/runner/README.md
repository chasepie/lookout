# Runner

The runner executes Scripts for Lookout. The .NET backend sends it a Script's source, and it runs the Script against a real browser and responds with the outcome. The backend owns everything else: scheduling, Baselines, Changes and notifications ([ADR-0001](../../docs/adr/0001-dotnet-orchestrates-node-executes-scripts.md)). It is used for both Runs and Test runs.

The runner holds no state. In deployment it runs in its own container, with no internet access and nothing mounted into it ([ADR-0003](../../docs/adr/0003-scripts-run-in-an-offline-runner-container.md)).

## Running it

```sh
npm start -w src/runner                  # http://localhost:3000, with a visible browser
HEADLESS=true npm start -w src/runner    # headless, as the tests run it
```

| Variable   | Default | Meaning |
|------------|---------|---------|
| `PORT`     | `3000`  | The port to listen on. `0` picks a free one. |
| `HEADLESS` | unset   | `true` runs Chromium headless. Otherwise it is visible, for debugging Scripts locally. |

On startup it logs `Runner listening on http://localhost:<port>`.

## API

### `POST /execute`

Runs one Script and responds once it has finished.

```json
{
  "source": "export default { async run(page, context) { … } };",
  "fileKind": "ts",
  "context": { "notificationUrl": "https://example.com/product" },
  "timeoutMs": 120000,
  "proxy": { "server": "http://gluetun:8888", "username": "…", "password": "…" }
}
```

| Field | |
|-------|-|
| `source` | The Script file's contents. |
| `fileKind` | `ts` or `js`. TypeScript runs through Node's type stripping, so it may only use erasable syntax (no `enum`, `namespace` or parameter properties). |
| `context` | Passed to the Script's `run` as its second argument. |
| `timeoutMs` | 1 to 2147483647. The Script's process is killed at this point. |
| `proxy` | Optional. The proxy for the Script's browser context, as Playwright's `proxy` option: `server`, and optionally `username`, `password` and `bypass`. VPN Runs set this, and direct Runs leave it out. |

A malformed request gets a `400` with `{ "error": "<what is wrong>" }`. A failure inside the runner itself gets a `500` with `{ "error": "Internal runner error" }`, and any other route a `404`.

Otherwise the response is a `200`, whatever the Script did:

```json
{ "outcome": "succeeded", "content": "£20.00", "durationMs": 1432 }
```

| `outcome` | When | Also has |
|-----------|------|----------|
| `succeeded` | `run` resolved to `{ success: true }` | `content`, `details` if the Script gave them |
| `knownFailure` | `run` resolved to `{ success: false }` | `content`, `details` if the Script gave them |
| `errored` | The Script threw, resolved to something that isn't a `ScriptOutcome`, didn't default-export a `LookoutScript`, exited its process, or ran past its timeout | `errorMessage` |

`durationMs` is always present.

## Writing a Script

A Script file default-exports either a `LookoutScript` object or a class implementing one. Both types come from `@chasepie/lookout-scripting`:

```ts
import type { LookoutScript } from '@chasepie/lookout-scripting';

export default {
  async run(page, context) {
    await page.goto(context.notificationUrl);
    return { success: true, content: await page.locator('.price').innerText() };
  },
} satisfies LookoutScript;
```

A Script may import `playwright` and Node built-ins. [`examples/`](../../examples) has a Script with its own test.

## How an execution works

```text
server.ts           POST /execute → validate the request
execute.ts          write the source to a fresh temp directory
                    fork script-process.ts, kill it at timeoutMs
  script-process.ts   import the Script and check its default export
                      launch Chromium (--disable-blink-features=AutomationControlled)
                      run(page, context) in a new browser context, with the request's proxy
                      check the outcome, close the browser (and with it the context)
                      report the result over IPC, then exit
execute.ts          delete the temp directory → respond
```

- **One child process per execution**, so a Script can't affect another or the runner itself. The child exits as soon as it has reported, even if the Script left a timer or socket open.
- **Results travel over IPC, not stdout,** so a Script's `console.log` can't be mistaken for its result. The Script shares that IPC channel, so each execution gets a random token that the child's report must carry back. The token stays private to `script-process.ts`, which also keeps its own reference to `process.send` from before the Script loads, so the Script can neither forge a report nor rewrite the real one. The message types are in `protocol.ts`.
- **The temp directory holds everything an execution writes.** That includes the Script file and, through the child's `TMPDIR`, Chromium's profile and Playwright's artifacts. A process killed at its timeout can't clean up after itself, so the runner deletes the whole directory.
- **`playwright` imports resolve from the runner.** The Script's file sits in a temp directory with no `node_modules`, so a resolve hook in `script-process.ts` points `playwright` imports at the runner's own copy.

Still to come:

- **#4:** a memory cap and `--permission` file-read limits on the child, a clear error for imports other than built-ins and `playwright`, and console output and screenshots in the response.
- **#13:** connecting to Browserless in deployment, instead of launching a local Chromium.

## Tests

```sh
npm test -w src/runner
```

`runner.spec.ts` is Seam 2. It starts a real runner process with `HEADLESS=true`, serves its own pages and proxy from local servers, and posts the Scripts in `test/fixtures/` to `/execute`. No internet is needed.

- **Temp files:** the runner gets its own `TMPDIR`, so a test can check that nothing is left behind after an execution, even one killed at its timeout.
- **Visible-browser test:** this one starts a second runner with `HEADLESS=false`. It is skipped on Linux when there's no `DISPLAY`.
- **Fixtures:** the Scripts are TypeScript, except `read-price.js`, which checks that JavaScript Scripts work.
