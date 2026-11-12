---
name: run-lookout
description: Run, start, stop and drive the real Lookout app (Aspire AppHost with Backend, frontend, Runner, browserless and gluetun) on throwaway data; create watches, queue runs, watch them run in parallel, switch the VPN region, and screenshot the UI. Use to verify a change end to end, beyond the test suite.
---

# Run Lookout

The AppHost (`src/AppHost`) starts everything: the Backend at `https://localhost:7070`, the Angular frontend at
`http://localhost:4200`, and the Runner, browserless and gluetun in Docker. Agents drive it with
`.claude/skills/run-lookout/lookout.sh`, which starts it detached on throwaway data, talks to the Backend's API with
`curl`, and screenshots the UI with Playwright. Paths here are relative to the repo root; run everything from there.

## Prerequisites

- Docker running, the Aspire CLI (`aspire`), the .NET SDK and Node, with `npm install` done at the repo root
  (`screenshot.mjs` imports `playwright` from `node_modules`).
- A `.env` at the repo root with `LOOKOUT_OWNER_USERNAME` and `LOOKOUT_OWNER_PASSWORD`, and, for VPN watches,
  `GLUETUN_OPENVPN_USER` and `GLUETUN_OPENVPN_PASSWORD` (see the README's settings table).

## Run (agent path)

```bash
L=.claude/skills/run-lookout/lookout.sh
$L start --limit 3        # ~20s once images are built; signs in. --limit sets LOOKOUT_MAX_CONCURRENT_RUNS (default here 3)
a=$($L watch vpn-a true .claude/skills/run-lookout/watch-scripts/slow-ip.ts)        # prints the watch's id
e=$($L watch direct-e false .claude/skills/run-lookout/watch-scripts/slow-ip.ts)
f=$($L watch failover true .claude/skills/run-lookout/watch-scripts/asks-for-failover.ts)
$L run $a $e              # queues a run of each
$L runs $a $e             # status, start/end, VPN region, retryOf, content of each watch's latest runs
$L switch                 # queues a VPN region switch (random region; or: $L switch "US Texas")
$L vpn                    # current region, switch in progress (waitingForRun), public IP, last error
$L wait-idle $a $e $f     # until none has a run waiting or running and no switch is in progress
$L screenshot .docker-volumes/run-lookout/watches.png /       # signs in through the UI; any route: /vpn, /watches/3
$L api GET /api/Watches   # any other API call, with the session cookie and CSRF headers
$L stop                   # stops the AppHost and waits for its containers
```

Then look at the screenshots with the Read tool. `start` wipes the throwaway data each time (`--keep-data` keeps it).
The development seeder adds `bot-detection` and `pia-location` watches to the fresh database.

The sample scripts in `watch-scripts/`:
- `slow-ip.ts` looks up its public IP and location on ipinfo.io, then holds its browser session for 25 seconds, so runs
  overlap. Its content shows where the run came from (the VPN region, or your own connection) and when it started and
  ended.
- `asks-for-failover.ts` holds its session for 10 seconds, then returns a known failure with `failover: true`. A VPN
  watch running it fails the VPN over and gets retried, three times in a row at most.

## Run (human path)

`dotnet run --project src/AppHost` opens the Aspire dashboard in the browser and runs in the foreground with your own
`.env` data until Ctrl-C. It uses your development database, not throwaway data.

## Gotchas

- **The AppHost reads `.env` only, once, at start**: process environment variables don't reach the services. So
  `start` appends a marked block to `.env` (the limit and the throwaway `LOOKOUT_DATABASE_DIR` and
  `LOOKOUT_WATCH_SCRIPTS_DIR` under `.docker-volumes/run-lookout/data`), and removes it as soon as `aspire run --detach`
  returns. `stop` removes it too, in case `start` was interrupted. If `.env` ever has a `# >>> run-lookout` block left
  in it, delete it.
- **The throwaway data lives under `.docker-volumes/`** on purpose: it's gitignored, and Docker Desktop shares it, so
  the Runner can bind-mount the scripts directory.
- **Never `rm -rf` and recreate the scripts directory just before starting.** Docker Desktop's file sharing went on
  seeing the recreated directory as missing (`bind source path does not exist: /host_mnt/...`). The Runner then failed
  to start, and wasn't retried, so the Backend waited for it forever. `start` empties the directories instead.
  If a resource still fails to start, `start` stops waiting and names it; `aspire logs <name> --apphost src/AppHost`
  shows why.
- **Right after `start`, the Backend makes its first VPN switch**, so VPN runs queued straight away stay `Created`
  until it's done (`$L vpn` shows the switch in progress).
- **The Backend picks up switch requests, and starts runs, about once a second.** `switch` waits until its switch has
  begun, and `wait-idle` needs to see nothing waiting or running twice, 2 seconds apart, before it returns.
- **`frontend-browser` always shows `FailedToStart`** in `aspire describe` and the dashboard: it's the VS Code browser
  debugger, and fails outside VS Code. Ignore it.
- **`aspire run --detach` returns before the services are up.** `start` then waits for `/api/auth/me` to answer 401 and
  the frontend to answer 200.
- **Requests that change data need `X-Lookout-Request` and a same-origin `Origin`** (`https://localhost:7070`),
  or the Backend's CSRF check refuses them. `lookout.sh api` sends both.
- **`aspire stop` returns before the containers are gone**; gluetun takes a few seconds more. `stop` waits for them.
  `SIGINT` to `dotnet run --project src/AppHost` didn't stop it either; `SIGINT` to its `aspire run` child did.
- **The Backend's logs aren't in `.docker-volumes/run-lookout/apphost.log`.** That only has the CLI's output; the
  services' logs are in the Aspire dashboard, whose URL with its login token is in that log.

## Troubleshooting

- `$L vpn` shows `lastError: Switched to US …, but the tunnel wasn't back up after 2 minutes`: the new tunnel didn't
  come up in time, which happened with one of the provider's regions. A failover that ends this way doesn't retry its
  watch, so an `asks-for-failover.ts` watch can stop short of 3 retries, and `wait-idle` takes the 2 minutes. Switch again.
- The Watches page in a screenshot shows "Hasn't run yet" next to "Running now": that's right, it means the watch has no
  finished run yet.
