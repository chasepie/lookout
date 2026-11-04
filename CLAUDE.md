# Lookout

A monorepo with a .NET solution (`Lookout.slnx`) and npm workspaces (root `package.json`).

## Layout

- `src/Backend/`: ASP.NET Core API that manages watches and their runs. It calls the Runner over HTTP (`LOOKOUT_RUNNER_URL`). It also rotates the VPN between regions through gluetun's control server (`VpnServices/`); the region list comes from gluetun's servers list in `LOOKOUT_GLUETUN_DIR`. The SQLite schema is managed with EF Core migrations in `Database/Migrations`: after changing the model, run `dotnet tool restore` once, then `dotnet ef migrations add <Name> --project src/Backend --output-dir Database/Migrations`.
- `src/Runner/`: ASP.NET Core service that runs watch scripts.
- `src/Core/`: types shared by Backend, Runner and AppHost: the C# records, the environment variable names (`LookoutEnvironmentVariables`) and the watch scripts directory (`WatchScriptsDirectory`).
- `src/AppHost/`: Aspire AppHost. `dotnet run --project src/AppHost` starts Runner, Backend, the frontend and browserless (Chromium for watch runs) and, when the OpenVPN credentials are in `.env`, gluetun (the VPN proxy for watches with `UseVpn`). It sets `LOOKOUT_RUNNER_URL`, `LOOKOUT_GLUETUN_URL`, `LOOKOUT_GLUETUN_API_KEY` and `LOOKOUT_VPN_PROXY_URL` for Backend and `LOOKOUT_BROWSERLESS_URL` and `LOOKOUT_VPN_PROXY_URL` for Runner, forwards the directory settings from `.env` (relative paths resolve against the repo root), and opens the Aspire dashboard. It bind-mounts `LOOKOUT_GLUETUN_DIR` as gluetun's data directory and generates a new control-server API key on each start.
- `src/ServiceDefaults/`: shared Aspire setup (OpenTelemetry, health checks, service discovery) referenced by Backend and Runner.
- `src/watch-sdk/`: TypeScript SDK and CLI (Playwright-based) for writing watch scripts.
- `src/frontend/`: Angular web UI. Its conventions are in `src/frontend/CLAUDE.md`.
- `examples/`: example watch scripts built on the SDK.
- `tests/`: MSTest projects for Backend and Runner.

Environment variables are loaded from `.env`. See `example.env` for the keys.

## TypeScript

Applies to every TypeScript workspace (`src/frontend`, `src/Runner`, `src/watch-sdk`, `examples`).

- Use strict type checking
- Let types be inferred when they are obvious
- Use `unknown` instead of `any` when a type is uncertain
