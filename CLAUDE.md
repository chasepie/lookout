# Lookout

A monorepo with a .NET solution (`Lookout.slnx`) and npm workspaces (root `package.json`).

## Layout

- `src/Backend/`: ASP.NET Core API that manages watches and their runs. It calls the Runner over HTTP (`LOOKOUT_RUNNER_URL`).
- `src/Runner/`: ASP.NET Core service that runs watch scripts.
- `src/Model/`: C# records shared by Backend and Runner.
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
