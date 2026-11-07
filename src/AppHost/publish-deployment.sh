#!/usr/bin/env bash
# Regenerates deploy/docker-compose.yaml and deploy/.env.example from the AppHost's publish mode (Production.cs).
# Run it after changing that, and commit the result: ProductionComposeTests fails while they differ.
set -euo pipefail
repo_root="$(cd "$(dirname "$0")/../.." && pwd)"
output="$(mktemp -d)"
trap 'rm -rf "$output"' EXIT

aspire publish --apphost "$repo_root/src/AppHost/AppHost.csproj" --output-path "$output" --non-interactive --nologo
cp "$output/docker-compose.yaml" "$repo_root/deploy/docker-compose.yaml"
# Compose reads .env, which holds the deployment's values, so the generated one is only an example to copy
cp "$output/.env" "$repo_root/deploy/.env.example"
