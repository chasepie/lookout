# Lookout

A monorepo with a .NET solution (`Lookout.slnx`) and npm workspaces (root `package.json`).

## Layout

- `src/Backend/`: ASP.NET Core API that manages watches and their runs, under `/api`. It also serves the built frontend from `wwwroot`, which its image
(`src/Backend/Dockerfile`) builds in. Only the Owner can use it: they sign in with `LOOKOUT_OWNER_USERNAME` and `LOOKOUT_OWNER_PASSWORD` (`Auth/`), and
outside `localhost` it must be served over HTTPS, behind a reverse proxy in production (`Hosting/`); see `docs/security.md`. It calls the Runner over HTTP
(`LOOKOUT_RUNNER_URL`). It also rotates the VPN between regions through gluetun's control server (`VpnServices/`); the region list comes from gluetun's
servers list in `GLUETUN_DATA_DIR`. The SQLite schema is managed with EF Core migrations in `Database/Migrations`: after changing the model, run
`dotnet tool restore` once, then `dotnet ef migrations add <Name> --project src/Backend --output-dir Database/Migrations`. In Development,
`DevelopmentDataSeeder` gives a database with no watches one watch per script in `examples/`.
- `src/Runner/`: ASP.NET Core service that runs watch scripts, sandboxed with Node's permission model in a container built from `src/Runner/Dockerfile`
(see `docs/security.md`). Without the AppHost it needs `BROWSERLESS_URL` and `BROWSERLESS_TOKEN`, and finds the watch CLI through the
repo root.
- `src/Core/`: types shared by Backend, Runner and AppHost: the C# records, the environment variable names (`EnvVars`), the watch
scripts directory (`WatchScriptsDirectory`), the key the Backend authenticates to the Runner with (`RunnerApiKey`) and the repo root lookup (`RepoRoot`).
- `src/AppHost/`: Aspire AppHost, in two halves.
  - `dotnet run --project src/AppHost` (`Development.cs`, which needs Docker) starts Runner, Backend, the frontend and browserless (Chromium for watch
    runs) and, when the OpenVPN credentials are in `.env`, gluetun (the VPN proxy for watches with `UseVpn`). It builds the Runner's container from
    `src/Runner/Dockerfile` and mounts the scripts directory into it read-only. It forwards the Owner's credentials and the directory settings from `.env`
    (relative paths resolve against the repo root), refusing to start without the credentials, sets the services' URLs, generates new secrets on each
    start, bind-mounts `GLUETUN_DATA_DIR` as gluetun's data directory (running gluetun's updater there first if it has no servers list), and opens the Aspire dashboard.
  - `aspire publish` (`Production.cs`) describes the production deployment as `deploy/docker-compose.yaml`. After changing it, run
    `src/AppHost/publish-deployment.sh` and commit the result, which `AppHost.Tests` checks is current.
- `src/ServiceDefaults/`: shared Aspire setup (OpenTelemetry, health checks, service discovery) referenced by Backend and Runner.
- `src/watch-sdk/`: TypeScript SDK and CLI (Playwright-based) for writing watch scripts.
- `src/frontend/`: Angular web UI. Its conventions are in `src/frontend/CLAUDE.md`.
- `deploy/`: the generated production deployment, and how to run it (`deploy/README.md`).
- `examples/`: example watch scripts built on the SDK.
- `tests/`: MSTest projects for Backend, Runner and AppHost. The Runner's tests run real scripts, so they need Node and the built watch SDK
(`npm run build -w src/watch-sdk`). `AppHost.Tests` publishes in-process and checks the Compose file.

Environment variables are loaded from `.env`. `example.env` lists only the ones you set in development; leave out the ones the AppHost always sets
itself, such as service URLs and generated keys. `EnvVars` names every variable the services read. Production's settings,
secrets included, are listed in `deploy/.env.example`, which `Production.cs` generates; the ones only Compose reads, such as `LOOKOUT_IMAGE_TAG`, are
named only there.

The domain's terms are in `GLOSSARY.md`, and decisions that would surprise a reader are recorded in `docs/adr/`. How signing in, sessions and CSRF
protection work, and the constraints they put on changes and deployment, are in `docs/security.md`: read it before changing anything in `Auth/`, the
request pipeline in `Program.cs`, or the frontend's `auth/`.

## TypeScript

Applies to every TypeScript workspace (`src/frontend`, `src/Runner`, `src/watch-sdk`, `examples`).

- Use strict type checking
- Let types be inferred when they are obvious
- Use `unknown` instead of `any` when a type is uncertain

## Agent skills

### Issue tracker

Issues are tracked in GitHub Issues on `chasepie/lookout`, using the `gh` CLI. See `docs/agents/issue-tracker.md`.

### Triage labels

The five default triage labels are used as-is (`needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`). See
`docs/agents/triage-labels.md`.

### Domain docs

Single-context: one root `GLOSSARY.md` and `docs/adr/`. See `docs/agents/domain.md`.
