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
| `PORT`     | `3000`  | The port to listen on, 0 to 65535. `0` picks a free one. Anything else stops the runner at startup. |
| `HEADLESS` | unset   | `true` runs Chromium headless. Otherwise it is visible, for debugging Scripts locally. |

On startup it logs `Runner listening on http://localhost:<port>`.

## API

### `POST /execute`

Runs one Script and responds once it has finished. If the caller disconnects first, the Script is stopped.

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
| `timeoutMs` | 1 to 2147483647. It covers the whole execution, including starting the process and launching the browser, not just `run`. Past it, the Script's process gets up to 5 seconds to report a screenshot, and is then killed. |
| `proxy` | Optional. The proxy for the Script's browser context, as Playwright's `proxy` option: `server`, and optionally `username`, `password` and `bypass`. VPN Runs set this. Direct Runs leave it out or send `null`. |

A malformed request gets a `400` with `{ "error": "<what is wrong>" }`, and a body over 1 MiB a `413`. A failure inside the runner itself gets a `500` with `{ "error": "Internal runner error" }`, and any other route a `404`.

Otherwise the response is a `200`, whatever the Script did:

```json
{ "outcome": "succeeded", "content": "£20.00", "durationMs": 1432, "consoleOutput": "" }
```

| `outcome` | When | Also has |
|-----------|------|----------|
| `succeeded` | `run` resolved to `{ success: true }` | `content`, `details` if the Script gave them |
| `knownFailure` | `run` resolved to `{ success: false }` | `content`, `details` if the Script gave them |
| `errored` | The Script threw, resolved to something that isn't a `ScriptOutcome`, or didn't default-export a `LookoutScript`. Also when, while `run` was in progress, it threw from a timer or event listener or left a promise rejection unhandled. Also when it imported something it may not, read a file it may not, exceeded its memory limit, ran past its timeout, or its process exited or was killed. | `errorMessage` |

Every response also has:

- **`durationMs`:** like `timeoutMs`, it covers the whole execution. For a Script that ran past its timeout, that includes the time taken to screenshot it.
- **`consoleOutput`:** what the Script's process wrote to stdout and stderr, which may be empty. The two are interleaved in the order they reached the runner, which needn't be the order they were written in. Past 262,144 characters (256 Ki) only the end is kept, after the line `[Earlier output was cut]`.

A `knownFailure` or `errored` response also has a `screenshot` of the Script's page, as a base64 PNG of its viewport, if the Script got as far as having a page and its process could still take one. A Script whose default export is wrong, or one that ran out of memory or blocked its process until its timeout, has none.

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

A Script's file may import only `playwright` and Node built-ins. Importing anything else, including a file of its own, is Errored with `Scripts can only import Node built-ins and playwright, not '<specifier>'`. `@chasepie/lookout-scripting` only holds types, and either form of type import from it works: `import type { … }`, or `import { type … }`. [`examples/`](../../examples) has a Script with its own test.

### Limits

| Limit | What happens |
|-------|--------------|
| `timeoutMs` | Errored with `Script timed out after <timeoutMs> ms`. |
| 512 MB heap (`--max-old-space-size`) | Errored with `Script exceeded its memory limit of 512 MB`. |
| File reads | Through Node's `--permission`, a Script may read only its own temp directory, the runner's code and `node_modules`, and Playwright's browsers. It may write only to its temp directory. Anything else is Errored with Node's `ERR_ACCESS_DENIED` message. |

## How an execution works

```text
server.ts           POST /execute → validate the request
execute.ts          write the source to a fresh temp directory
                    fork script-process.ts with a memory limit and --permission, collecting its stdout and stderr
                    at timeoutMs, ask it for a screenshot, then kill it
  script-process.ts   import the Script, limiting its imports, and check its default export
                      launch Chromium (--disable-blink-features=AutomationControlled)
                      run(page, context) in a new browser context, with the request's proxy
                      check the outcome, screenshot the page unless it Succeeded
                      close the browser (and with it the context)
                      report the result over IPC, then exit
execute.ts          delete the temp directory → respond
```

- **One child process per execution**, so a Script can't affect another or the runner itself. The child exits as soon as it has reported, even if the Script left a timer or socket open. It also exits if the runner goes away, so a Script that never finishes can't outlive it.
- **Errors outside `run`'s own promise** count as long as `run` is in progress. That covers a throw from a timer or event listener, or a rejection the Script left unhandled. The child races `run` against them, so the error becomes the Errored message rather than a crash that loses it. Once `run` has settled they're ignored, so a wait the Script left pending can't fail the Run when the browser closes.
- **Results travel over IPC, not stdout,** so a Script's `console.log` can't be mistaken for its result. The Script shares that IPC channel. So each execution gets a random token that the child's report must carry back, and the child keeps its own reference to `process.send` from before the Script loads. That way a Script's own use of the channel, or of `process.send`, can't be taken for or rewrite the report. These keep a Script from interfering by mistake, not on purpose. Code in the same process can always reach the channel, but it needn't be stopped from forging its outcome, since a Script decides its own outcome anyway. The message types are in `protocol.ts`.
- **The temp directory holds everything an execution writes.** That includes the Script file and, through the child's `TMPDIR`, Chromium's profile and Playwright's artifacts. A process killed at its timeout can't clean up after itself, so the runner deletes the whole directory.
- **`playwright` imports resolve from the runner.** The Script's file sits in a temp directory with no `node_modules`, so a resolve hook in `script-process.ts` points `playwright` imports at the runner's own copy, and refuses any import other than a Node built-in. It applies only to imports made by the Script's own file, not to those `playwright` makes. Imports from `@chasepie/lookout-scripting` that survive type stripping get an empty module.
- **A Script that runs past its timeout gets a screenshot.** The runner sends the child a `timedOut` message, and the child reports at once with a screenshot of the Script's page. The runner keeps only the screenshot, words the timeout itself, and kills the child as soon as it has reported, or after 5 seconds if it can't, as when the Script blocks its process.
- **Running out of memory is told apart by its output.** V8 aborts a process that reaches its heap limit, writing `JavaScript heap out of memory` to stderr. A process that ends that way without reporting is Errored as having exceeded its memory limit, rather than as killed by `SIGABRT`.
- **The file limits are not a security boundary.** Node says its permission model isn't one. In particular, the child must be allowed to start processes, because Playwright starts Chromium as one, and a Script can use that to get around them. The runner's offline container is what contains a Script ([ADR-0003](../../docs/adr/0003-scripts-run-in-an-offline-runner-container.md)). Node checks paths as given, without resolving symlinks, so the runner gives the child its temp directory's real path, as the Script's module path is.

Still to come:

- **#13:** connecting to Browserless in deployment, instead of launching a local Chromium.

## Tests

```sh
npm test -w src/runner
```

`runner.spec.ts` is Seam 2. It starts a real runner process with `HEADLESS=true`, serves its own pages and proxy from local servers, and posts the Scripts in `test/fixtures/` to `/execute`. No internet is needed.

- **Temp files:** the runner gets its own `TMPDIR`, so a test can check that nothing is left behind after an execution, even one killed at its timeout.
- **Visible-browser test:** this one starts a second runner with `HEADLESS=false`. It is skipped on Linux when there's no `DISPLAY`.
- **Fixtures:** the Scripts are TypeScript, except `read-price.js`, which checks that JavaScript Scripts work.
