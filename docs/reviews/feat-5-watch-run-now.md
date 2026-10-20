# Code review: `feat/5-watch-run-now`

Review of `git diff main...HEAD` on branch `feat/5-watch-run-now` (commits `e8618f7` and `2f1b7d9`), which implements Watch creation and on-demand Runs (issue #5).

## How to use this file

- **All findings have now been triaged.** Status lines record reproduced bugs, completed fixes, and scoped deferrals. The original automated findings are retained for traceability.
- Fix the findings in the order of the priority list below. Findings in the same group are independent of each other unless a note says otherwise.
- Each fix that changes behavior needs a test that fails without the fix: an integration test in `tests/Backend.IntegrationTests` for backend changes, or a Playwright spec under `src/frontend` for UI changes.
- Follow `CLAUDE.md`: Node code is TypeScript, and domain terms come from `GLOSSARY.md`.
- When you finish a finding, set its **Status** line to `Fixed (<commit>)`, `Not a bug: <reason>`, or `Deferred: <reason>`.

## Priority

1. **Fix first:** 1, 2
2. **Fix next:** 4, 5, 7, 8, 9, 12
3. **Ask the user first:** 10, 11 (each depends on a product decision; see its note)
4. **Tests and CI:** 13, 14, 15
5. **Future risk, may defer:** 3, 6 (these matter once scheduled Runs or Watch deletion exist)
6. **Cleanup:** 16 to 20

---

## 1. A Run is discarded when the client disconnects

- **Location:** `src/Backend/WatchesController.cs:124`
- **Problem:** Run now passes `HttpContext.RequestAborted` to the runner client and to `SaveChangesAsync`.
- **Scenario:** A user clicks Run now on a 60-second Script, then reloads or closes the tab. `RequestAborted` fires, `HttpRunnerClient` rethrows the `OperationCanceledException`, and the runner's `callerGone` handler stops the Script. No Run row is saved and the Baseline doesn't move, yet a Run should always be recorded.
- **Suggested fix:** Run on a token that isn't tied to the request, such as `IHostApplicationLifetime.ApplicationStopping`.
- **Done when:** A test shows that cancelling the HTTP request mid-Run still saves a Run row.
- **Status:** Fixed (53689da): Run execution and saving now use the application lifetime token. The API regression cancels the caller, completes the fake runner, and verifies the stored Run and Baseline through REST.

## 2. The SPA fallback answers unmatched `/api` requests with 200 HTML

- **Location:** `src/Backend/Program.cs:77`
- **Problem:** `MapFallbackToFile("index.html")` matches every HTTP method and every unmatched path, including `/api/*`.
- **Scenario:** `GET /api/watches/not-a-guid/runs`, `GET /api/watches/{id}` and `DELETE /api/watches` all return `200 text/html` instead of 404 or 405.
- **Suggested fix:** Scope the fallback to paths outside `/api`, for example with `MapFallbackToFile("{*path:nonfile}", ...)` plus an `/api` exclusion, or a route constraint. Also limit it to GET.
- **Done when:** Tests show that an unknown `/api` path returns 404, a wrong method on a known `/api` route returns 405, and a client-side route such as `GET /some-page` still returns `index.html`.
- **Status:** Fixed (53689da): the SPA route excludes /api (case-insensitively). Tests cover API 404s, wrong-method 405s, and normal page fallback. The original framework fallback already limited methods to GET/HEAD, so the review's claim that every method returned HTML was not reproduced.

## 3. Overlapping Runs can overwrite each other's Baseline

- **Location:** `src/Backend/RunningWatches.cs:9`
- **Problem:** The guard against overlapping Runs is an in-memory dictionary that covers only on-demand Runs. Nothing prevents a lost update on `Watch.Baseline`.
- **Scenario:** Once scheduled Runs (#6, #7) or a second backend instance exist, two Runs can read the same Baseline and both write it back. One Change is missed or recorded twice.
- **Suggested fix:** Add a concurrency token to `Watch`, or update the Baseline only if it still has its prior value, so the database enforces this for every source of Runs.
- **Note:** Not reachable today. Deferring it to #6 or #7 is fine; if you defer, note it on that issue.
- **Status:** Deferred: there is currently one backend and one Run dispatch path, protected by RunningWatches. Scheduled dispatch in #7 must share admission control or add database concurrency protection before introducing another writer. The second-instance scenario is outside this slice; this handoff is recorded here.

## 4. The leading slash in `"/execute"` drops any path in `Runner:BaseUrl`

- **Location:** `src/Backend/Runner/HttpRunnerClient.cs:19`
- **Problem:** A relative URI that starts with `/` replaces the base address's path.
- **Scenario:** With `Runner__BaseUrl=http://proxy/runner/`, requests go to `http://proxy/execute`, and every Run is recorded as Errored with a 404.
- **Suggested fix:** Use `"execute"` with no leading slash, and make sure the base address ends with `/`. Add the slash when configuring the client if it's missing.
- **Done when:** A test shows that a base URL with a sub-path reaches `<base>/execute`.
- **Status:** Fixed (53689da): execute is resolved relative to a base URL normalized with a trailing slash. Integration tests exercise a /runner prefix both with and without the trailing slash.

## 5. The runner's error body, console output and screenshot are lost

- **Location:** `src/Backend/Runner/HttpRunnerClient.cs:20`
- **Problem:** `EnsureSuccessStatusCode` throws away the response body. Separately, `RunnerResult` has no fields for the `consoleOutput` and `screenshot` that the runner returns (added in #4).
- **Scenario:** The runner rejects a request with `400 {"error":"timeoutMs must be ..."}` or `413`, and the Run's error reads only "Response status code does not indicate success: 400". For Errored and Known failure Runs, the debug output the runner sends back is dropped.
- **Suggested fix:** For non-success responses, read the body and include the runner's `error` field in the Run's error. Add console output and a screenshot to `RunnerResult` and store them on the Run. Storing them may need a migration; check the Run entity in `src/Database`.
- **Done when:** Tests show that a runner 400 response saves its error message on the Run, and that console output from a failing Script is saved.
- **Status:** Deferred: console-output and screenshot persistence belongs to #12, which explicitly includes storage, presentation, and retention. The independently confirmed loss of HTTP error bodies is fixed (53689da); a runner 400 error message is now recorded and regression-tested.

## 6. Deleting a Watch that has Runs can fail on a foreign key

- **Location:** `src/Database/LookoutDbContext.cs:34`
- **Problem:** The Runs-to-ScriptVersions foreign key is RESTRICT, while deleting a Watch cascades to both Runs and ScriptVersions.
- **Scenario:** Once a delete-Watch endpoint exists, deleting a Watch with history can fail with "FOREIGN KEY constraint failed", depending on the order SQLite applies the cascades.
- **Suggested fix:** Make Runs-to-ScriptVersions cascade, or delete Runs explicitly before ScriptVersions. Either way needs a migration.
- **Note:** Not reachable until Watch deletion exists. Deferring it is fine.
- **Status:** Deferred: Watch deletion is introduced by #8. No current endpoint can reach this cascade scenario. The delete implementation must test a Watch with Run history and choose safe delete ordering or cascade rules; this handoff is recorded here.

## 7. `NotificationUrl` is stored without trimming

- **Location:** `src/Backend/WatchesController.cs:61`
- **Problem:** Validation uses `Uri.TryCreate`, which trims whitespace, but the original string is stored.
- **Scenario:** Posting `notificationUrl: " https://example.com/widget\n"` passes validation and stores the padded string. That string becomes the link's `href` and is passed to the Script as `context.notificationUrl`, where `page.goto` may fail.
- **Suggested fix:** Store the parsed URI's `AbsoluteUri`, or the trimmed input.
- **Done when:** A test shows that a URL with surrounding whitespace is stored without it.
- **Status:** Fixed (53689da): Notification URL input is trimmed before storage. An API test verifies the returned and persisted URL plus the context delivered to the runner.

## 8. A Succeeded result with no content is only checked in one runner client

- **Location:** `src/Model/RunEvaluation.cs:13`
- **Problem:** Only `HttpRunnerClient` checks that a Succeeded result has content. `RunEvaluation` throws `ArgumentNullException` when it gets one without content.
- **Scenario:** Any other `IRunnerClient` (the test fake, or a future client) that returns `Succeeded` with `Content = null` makes Run now return 500, and no Run is saved.
- **Suggested fix:** Check this where the result is evaluated: treat a Succeeded result without content as Errored with a clear message, instead of throwing. Then remove the duplicate check from `HttpRunnerClient`.
- **Done when:** A test with the fake client returning Succeeded and null content records an Errored Run.
- **Status:** Fixed (53689da): pure Run evaluation turns a Succeeded result without content into Errored, preserving the Baseline. The duplicate HTTP-client content check was removed; fake-runner tests cover this with and without an existing Baseline.

## 9. A Watch with no current Script version crashes Run now

- **Location:** `src/Backend/WatchesController.cs:118`
- **Problem:** `watch.CurrentScriptVersion!` uses `!` on a nullable navigation.
- **Scenario:** A Watch whose `CurrentScriptVersionId` is null causes a NullReferenceException and a 500.
- **Suggested fix:** Return a clear 409 or 422 ProblemDetails when there is no current Script version.
- **Done when:** A test covers this case and gets the chosen status code.
- **Status:** Fixed (53689da): Run now returns 409 ProblemDetails for a Watch with no current Script version. The regression arranges an incomplete legacy row and asserts the public response. Normal atomic Watch creation cannot produce this state.

## 10. `UseHttpsRedirection()` was removed

- **Location:** `src/Backend/Program.cs:70`
- **Problem:** The diff removes `app.UseHttpsRedirection()`, but `launchSettings.json` still has an https profile.
- **Scenario:** With the https profile, the app also serves plain HTTP on `localhost:5112` and doesn't redirect.
- **Note — ask the user first:** The removal may have been on purpose, for example for the e2e test host or for a deployment behind a TLS-terminating proxy. Ask whether to restore it, restore it only outside Development or tests, or remove the https profile instead.
- **Status:** Fixed (53689da): the user chose redirects when HTTPS is configured while retaining HTTP-only local operation. HTTPS redirection is restored; integration cases verify both configurations.

## 11. The status badge doesn't match `GLOSSARY.md`

- **Location:** `src/frontend/src/app/app.html:50`
- **Problem:** The badge shows "Pending" only when a Watch has no Runs, but the glossary defines Pending as "the Watch has no Baseline yet". The badge also shows Run outcomes such as "Succeeded", where the glossary's term for the Watch status is "Healthy" and it lists "Succeeded" under *Avoid*.
- **Scenario:** A new Watch whose first Run is Errored has no Baseline, so the glossary calls it Pending, but the card shows "Errored".
- **Note — ask the user first:** Decide whether the badge shows the **Watch status** (glossary terms) or the **last Run's outcome**. If the Watch status, the backend may need to return it, or enough data to work it out, in `WatchSummary`. Update the glossary if the decision changes a definition.
- **Status:** Fixed (53689da): the user chose the last Run's outcome. The badge now explicitly says Last Run: Succeeded/Known failure/Errored, or No Runs yet. Browser tests cover the first success and both first-failure outcomes. Watch status remains scoped to #8; no glossary definition changed.

## 12. The frontend replaces server error messages with generic ones

- **Location:** `src/frontend/src/app/app.ts:68` (both `catch` blocks)
- **Problem:** The `detail` from a 400 ProblemDetails, and a 409 "already running" response, are replaced with generic messages.
- **Scenario:** If a Run was started in another tab, Run now returns 409 and the user sees "Could not run the Watch. Please try again." A URL that passes the frontend's check but fails the backend's (such as `https://[`) shows "Check the fields" without saying which field.
- **Suggested fix:** Show the ProblemDetails `detail` (or the field errors) when there is one, and show a specific message for 409.
- **Done when:** A Playwright spec checks the message shown for a 409 and for a 400 from the backend.
- **Status:** Fixed (53689da): the UI displays ProblemDetails detail or field-error messages and keeps a fallback for other errors. Playwright covers a real backend 400 and presentation of a 409 response; the backend integration suite separately proves the real overlapping-Run 409.

## 13. `npm run typecheck` skips the frontend and `playwright.config.ts`

- **Location:** `tsconfig.json:3`
- **Problem:** The root tsconfig excludes `src/frontend`, so typecheck covers only `e2e/`.
- **Scenario:** A type error in `src/frontend/playwright.config.ts` passes typecheck and lint, because Playwright transpiles without checking types. Type errors in the app only show up later, in `ng build`.
- **Suggested fix:** Make `typecheck` also run `tsc --noEmit -p` on the frontend's tsconfig files (app and spec), and make sure one of them includes `playwright.config.ts`.
- **Status:** Fixed (53689da): typecheck now runs the Angular compiler for app TypeScript/templates and checks Playwright configuration alongside the e2e files. Temporary invalid-type probes in app.ts and playwright.config.ts both caused npm run typecheck to fail and were then removed.

## 14. The Playwright webServer timeout may be too short in CI

- **Location:** `src/frontend/playwright.config.ts:18`
- **Problem:** 60 seconds must cover a cold `dotnet run`, which restores and builds Model, Database, Backend and Frontend.TestHost. The CI node job doesn't build the solution first.
- **Scenario:** On a cold GitHub runner, Playwright gives up waiting for `/health` and the e2e step fails now and then.
- **Suggested fix:** Run a `dotnet build` step before e2e in CI and use `--no-build` in the webServer command, or raise the timeout, or both. Check the workflow under `.github/workflows`.
- **Status:** Fixed (53689da): test:e2e builds the backend test host before Playwright starts its readiness timer, and webServer uses --no-build. The same command is used locally and in CI.

## 15. Parallel e2e tests share one database

- **Location:** `src/frontend/playwright.config.ts:5`
- **Problem:** `fullyParallel` is on, but every test shares one backend and one SQLite database, and tests find Watches by name.
- **Scenario:** A second spec that also creates "Widget price" runs at the same time. `getByRole('article', { name: 'Widget price' })` matches two cards and fails in strict mode, or a test reads the other test's Run.
- **Suggested fix:** Give each test unique Watch names (for example, add the test ID), or turn off parallel runs, or use one backend per worker.
- **Status:** Fixed (53689da): every browser-test Watch name contains a fresh UUID. The expanded suite runs in parallel, and repeating each test twice verifies that repeated instances do not collide.

## 16. The repository-root lookup is duplicated

- **Location:** `tests/Backend.IntegrationTests/RunnerClientTests.cs:14`, and a copy in `LookoutWebApplicationFactory.cs:22`
- **Problem:** Both walk up the directory tree to find `Lookout.slnx`.
- **Suggested fix:** Move it into one static helper, such as `RepositoryRoot.Find()`, and call that from both places.
- **Status:** Fixed (53689da): RepositoryRoot.Find is shared by the runner integration test and WebApplicationFactory, including the linked frontend test-host code.

## 17. The `RunOutcome` JSON converter is configured twice

- **Location:** `src/Backend/Runner/HttpRunnerClient.cs:11`, and a copy in `src/Backend/Program.cs:29`
- **Problem:** `JsonStringEnumConverter<RunOutcome>(CamelCase, false)` is set up in both places. If one changes and the other doesn't, the runner protocol and the API disagree on how outcomes are spelled.
- **Suggested fix:** Define the converter once, as a shared static or a `[JsonConverter]` attribute on `RunOutcome`.
- **Status:** Fixed (53689da): RunOutcome has one JSON converter declaration used by both the REST API and runner adapter. Existing protocol and outcome integration tests cover its encoding and rejection of invalid values.

## 18. `WatchSummary.LatestContent` duplicates `Baseline`

- **Location:** `src/Backend/WatchesController.cs:28`
- **Problem:** The two fields always hold the same value.
- **Suggested fix:** Drop one and update the frontend to match, unless `LatestContent` is meant to be different (for example, the last Run's content even when it isn't the Baseline). If it's meant to be different, implement that instead.
- **Status:** Not a bug: Baseline is comparison state, while LatestContent is the presentation contract. Their equal values are intentional in this initial slice. Script-version Baseline resets in #11 can make them diverge, so removing either contract now would couple unrelated purposes.

## 19. Run history has no limit

- **Location:** `src/Backend/WatchesController.cs:87`
- **Problem:** `GET /api/watches/{id}/runs` returns every Run with its full content and details.
- **Suggested fix:** Add a default `Take` limit (newest first), or paging.
- **Status:** Fixed (53689da): history defaults to 50 Runs, accepts a limit from 1 to 100, and uses before=<Run ID> for older pages. An API regression creates 52 Runs and verifies both pages and the explicit limit.

## 20. The `201 Created` Location header points at the collection

- **Location:** `src/Backend/WatchesController.cs:73`
- **Problem:** `Location` is `/api/watches`, not the new Watch.
- **Suggested fix:** Point it at the new Watch's URI, or leave out `Location` until a get-by-ID endpoint exists.
- **Status:** Fixed (53689da): creation still returns 201 and the Watch body, but omits Location until a get-by-ID resource exists. The creation regression asserts that the collection is not advertised as the created resource.

## Follow-up validation

Fix commit: `53689da`. The full suite passed 73 runner tests, 49 .NET integration cases, and 5 Playwright tests. Repeating the Playwright suite twice with five workers passed all 10 runs. Typechecking, lint, and builds passed. Temporary invalid-type probes confirmed that both Angular app code and Playwright configuration are checked.

Sixteen findings are fully fixed. Finding 5 has its HTTP error-body bug fixed, with debug-artifact persistence deferred to #12. Findings 3 and 6 are future-work handoffs to #7 and #8. Finding 18 is intentional API design.
