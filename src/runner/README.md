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
| `BROWSERLESS_URL` | unset | Browserless's Playwright endpoint, such as `ws://browserless:3000/chromium/playwright`, which may include a `token`. Each Script then connects to its own headful Chromium there, as in deployment (the `connect` provider). Otherwise each Script launches a local Chromium (the `launch` provider). A URL that isn't `ws://` or `wss://` stops the runner at startup. |
| `HEADLESS` | unset   | `true` runs a launched Chromium headless. Otherwise it is visible, for debugging Scripts locally. Browserless's is always headful, on the virtual display its image starts. |

Either way, Chromium gets `--disable-blink-features=AutomationControlled`, so `navigator.webdriver` is false, and `--webrtc-ip-handling-policy=disable_non_proxied_udp`, so WebRTC can't reveal the home IP around a VPN Run's proxy. With `connect`, Chromium is headful, so its user agent says `Chrome` rather than `HeadlessChrome`, which bot detection takes for a robot on its own ([the prototype's findings](../../docs/research/browser-and-vpn-prototype.md)). The runner's Playwright must match Browserless's at the major.minor level.

In deployment, the runner runs from [`Dockerfile`](Dockerfile), which installs only `playwright` and no browser.

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
| `timeoutMs` | 1 to 2147483647. It covers the whole execution, including starting the process and launching the browser, not just `run`. An outcome the Script reached before it stands. Past it, a Script without one is Errored, and its process gets up to 5 seconds to send a screenshot before it's killed. |
| `proxy` | Optional. The proxy for the Script's browser context, as Playwright's `proxy` option: `server`, and optionally `username`, `password` and `bypass`. VPN Runs set this. Direct Runs leave it out or send `null`. |
| `alwaysScreenshot` | Optional, `false` when left out or `null`. Whether a `succeeded` response has a `screenshot` too. Test runs set this. |

A malformed request gets a `400` with `{ "error": "<what is wrong>" }`, and a body over 1 MiB a `413`. A request with an `Origin` header gets a `403`, and one whose `Content-Type` isn't `application/json` a `415`. Browsers send `Origin` with every POST, and a web page can only send JSON after a CORS preflight, which the runner never answers. So a page Browserless loads can't run a Script here, even one whose domain was rebound to the runner's address. A failure inside the runner itself gets a `500` with `{ "error": "Internal runner error" }`, and any other route a `404`.

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

A `knownFailure` or `errored` response, or any response to a request with `alwaysScreenshot`, also has a `screenshot` of the Script's page, as a base64 PNG of its viewport, if the Script got as far as having a page and its process could still take one. A Script whose default export is wrong, or one that ran out of memory or blocked its process until its timeout, has none.

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
| 512 MB V8 heap (`--max-old-space-size`) | Errored with `Script exceeded its memory limit of 512 MB`. This limits only what V8 manages: memory outside it, such as `Buffer`s, is limited only by the runner's container (`LOOKOUT_RUNNER_MEMORY` in the [main README](../../README.md#deploying)). |
| File reads | Through Node's `--permission`, a Script may read only its own temp directory, the runner's code and `node_modules`, and the system files Playwright reads on Linux as it's imported, to tell whether it's under WSL: `/proc/version`, `/proc/sys/fs/binfmt_misc/WSLInterop` and `/run/WSL`. With `launch`, it may also read Playwright's browsers and `/etc/os-release`, which Playwright reads to pick a Chromium build. System files are granted even where they don't exist, since under `--permission` even checking whether a file exists throws for a path that isn't granted. It may write only to its temp directory. Anything else is Errored with Node's `ERR_ACCESS_DENIED` message. |
| Processes | With `connect`, a Script may not start one: it's Errored with Node's `ERR_ACCESS_DENIED` message. With `launch`, it may, since Playwright starts Chromium as one. |

## How an execution works

```text
server.ts           POST /execute → validate the request
execute.ts          write the source to a fresh temp directory
                    fork script-process.ts with a memory limit and --permission
                    collect its stdout and stderr (console-output.ts)
                    at timeoutMs without an outcome, ask it for a screenshot, then kill it
  script-process.ts   import the Script, limiting its imports, and check its default export
    browser.ts          launch: launch Chromium → report its pid
                        connect: connect to a Chromium in Browserless
                        open a page in a new browser context, with the request's proxy
                      run(page, context)
                      check the outcome → report it
                      screenshot the page unless it Succeeded, close Chromium or the connection
                      write out its console output → report it's finished, with the screenshot, then exit
execute.ts          kill what the Script left running, delete the temp directory → respond
```

`browser.ts` holds what each provider means, on both sides: what a Script's process must be allowed to do for its browser, which the runner works out as it starts, and how the child opens a page in that browser.

`protocol.ts` holds both ends of what the runner and the child say to each other: a runner end that `execute.ts` passes the child's messages to, and a child end that `script-process.ts` reports through. The rules below about which report counts, the timeout and its 5 seconds of grace, when a screenshot is kept, and how an ending without an outcome is worded all live there.

- **One child process per execution**, so a Script can't affect another or the runner itself. The child exits as soon as it has reported, even if the Script left a timer or socket open. It also exits if the runner goes away, so a Script that never finishes can't outlive it.
- **Nothing a Script starts outlives its execution.** The child leads a process group of its own, which any subprocess the Script starts joins. Playwright gives Chromium a group of its own, so the child launches it with `launchServer`, the one API that tells which process it is, and reports its pid. When the child exits, or is killed at its timeout or because the caller went away, the runner kills both groups. If the runner itself goes away, the child kills them. If the runner is stopped by `SIGINT` or `SIGTERM`, it kills them first, since a Script that blocks its process can't notice the runner going. A runner killed outright with `SIGKILL` can't, and a Script that blocks its process then keeps running. A subprocess that leaves the group on purpose, as `spawn` with `detached: true` does, escapes all this, like the other limits that aren't a security boundary. This relies on POSIX process groups. On Windows, which has none, only the Script's own process is killed. With `connect`, there's no local Chromium: Browserless closes a Script's browser once its connection closes, however its process ended, and the Script can't start a subprocess. In the runner's container, a minimal init (`init: true`) is PID 1 and reaps the processes the runner kills, which Node as PID 1 wouldn't.
- **Errors outside `run`'s own promise** count as long as `run` is in progress. That covers a throw from a timer or event listener, or a rejection the Script left unhandled. The child races `run` against them, so the error becomes the Errored message rather than a crash that loses it. Once `run` has settled they're ignored, so a wait the Script left pending can't fail the Run when the browser closes.
- **Results travel over IPC, not stdout,** so a Script's `console.log` can't be mistaken for its result. The Script shares that IPC channel. So each execution gets a random token that the child's reports must carry back, and the child keeps its own reference to `process.send` from before the Script loads. That way a Script's own use of the channel, or of `process.send`, can't be taken for or rewrite the report. These keep a Script from interfering by mistake, not on purpose. Code in the same process can always reach the channel, but it needn't be stopped from forging its outcome, since a Script decides its own outcome anyway. Only `protocol.ts` sees the token: it stamps it on the job, the child's end stamps it on each report, and the runner's end ignores anything without it.
- **The temp directory holds everything an execution writes.** That includes the Script file and, through the child's `TMPDIR`, Chromium's profile and Playwright's artifacts. A process killed at its timeout can't clean up after itself, so the runner deletes the whole directory.
- **`playwright` imports resolve from the runner.** The Script's file sits in a temp directory with no `node_modules`, so a resolve hook in `script-process.ts` points `playwright` imports at the runner's own copy, and refuses any import other than a Node built-in. It applies only to imports made by the Script's own file, not to those `playwright` makes. Imports from `@chasepie/lookout-scripting` that survive type stripping get an empty module.
- **The outcome is reported as soon as there is one.** The screenshot and the console output follow in a last `finished` report. So the time they take can't push an outcome the Script reached in time past its timeout. Once the timeout passes, the child has 5 seconds to finish, whether or not it has an outcome. Its screenshot may take 2 of them and writing out its console output 1, then it's killed.
- **A Script that runs past its timeout gets a screenshot.** The runner sends the child a `timedOut` message, and the child finishes at once with a screenshot of the Script's page. The runner words the timeout itself. A child that can't finish, as when the Script blocks its process, is killed when the 5 seconds run out.
- **Running out of memory is told apart by how the process ends.** V8 aborts a process that reaches its heap limit with `SIGABRT`, writing `JavaScript heap out of memory` to stderr. A process that ends that way without reporting is Errored as having exceeded its memory limit, rather than as killed by `SIGABRT`. Both must hold, so a Script that only writes that message isn't taken to have run out of memory.
- **Console output can't hold up the response.** The child waits, for up to 1 second, for its console writes to reach the runner before it finishes and exits, since writes to a pipe are asynchronous. Once it has exited, the runner waits at most 1 second more for the end of its output, so a subprocess the Script started, which may share its stdout and stderr, can't keep the execution going.
- **The file limits are not a security boundary.** Node says its permission model isn't one. In particular, with `launch` the child must be allowed to start processes, because Playwright starts Chromium as one, and a Script can use that to get around them. With `connect` it isn't allowed to, which closes that gap. The runner's offline container is what contains a Script ([ADR-0003](../../docs/adr/0003-scripts-run-in-an-offline-runner-container.md)). Node checks paths as given, without resolving symlinks, so the runner gives the child its temp directory's real path, as the Script's module path is. The child doesn't get the runner's `NODE_OPTIONS`, since whatever it preloads, such as a debugger, is outside what the child may read.

## Tests

```sh
npm test -w src/runner
```

`runner.spec.ts` is Seam 2. It starts a real runner process with `HEADLESS=true`, serves its own pages and proxy from local servers, and posts the Scripts in `test/fixtures/` to `/execute`. No internet is needed.

`server.spec.ts` covers the HTTP API on its own: it starts `server.ts` in-process with a fake runner, so checking how each request is parsed, refused or rejected needs no child process or browser. `runner.spec.ts` still covers how the real runner starts up, and that a caller going away stops the Script and everything it started.

`protocol.spec.ts` wires a runner end and a child end to each other in memory, without a process or a browser. It covers the orderings that are hard to bring about end to end: an outcome arriving after the timeout, a second outcome or `finished`, a report a Script forged, and each way an ending without an outcome is worded. `runner.spec.ts` still covers what only a real child shows, such as a Script being unable to forge its report from inside its own process.

- **Temp files:** the runner gets its own `TMPDIR`, so a test can check that nothing is left behind after an execution, even one killed at its timeout.
- **Visible-browser test:** this one starts a second runner with `HEADLESS=false`. It is skipped on Linux when there's no `DISPLAY`.
- **`connect` tests:** these start a runner whose `BROWSERLESS_URL` is a browser server the test launches, which speaks the same protocol as Browserless, or a stand-in that records the URL the runner asks for. The real Browserless is covered by the manual smoke test in the [main README](../../README.md#smoke-test).
- **Fixtures:** the Scripts are TypeScript, except `read-price.js`, which checks that JavaScript Scripts work.
