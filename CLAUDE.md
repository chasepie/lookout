# Lookout

A monorepo with a .NET solution (`Lookout.slnx`) and npm workspaces (root `package.json`).

## Layout

- `src/Backend/`: ASP.NET Core API that manages watches and their runs. It calls the Runner over HTTP (`LOOKOUT_RUNNER_URL`).
- `src/Runner/`: ASP.NET Core service that runs watch scripts.
- `src/Core/`: types shared by Backend, Runner and AppHost: the C# records, the environment variable names (`LookoutEnvironmentVariables`) and the watch scripts directory (`WatchScriptsDirectory`).
- `src/AppHost/`: Aspire AppHost. `dotnet run --project src/AppHost` starts Runner, Backend and the frontend, sets `LOOKOUT_RUNNER_URL` for Backend, forwards the directory settings from `.env` (relative paths resolve against the repo root), and opens the Aspire dashboard.
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
