#!/usr/bin/env bash
# Regenerates deploy/docker-compose.yaml and deploy/.env.example from the AppHost's publish mode (Production.cs).
# Run it after changing that, and commit the result: ProductionComposeTests fails while they differ.
set -euo pipefail
repo_root="$(cd "$(dirname "$0")/../.." && pwd)"
output="$(mktemp -d)"
trap 'rm -rf "$output"' EXIT

# The AppHost's own publish step, as ProductionComposeTests runs it, so the two can't come out differently. Run
# directly rather than with `dotnet run` or `aspire publish`: the first stays open like a development run, and the second
# needs the Aspire CLI.
dotnet build "$repo_root/src/AppHost" --verbosity quiet
dotnet "$repo_root/src/AppHost/bin/Debug/net10.0/AppHost.dll" --operation publish --step publish --output-path "$output"
cp "$output/docker-compose.yaml" "$repo_root/deploy/docker-compose.yaml"
# Compose reads .env, which holds the deployment's values, so the generated one is only an example to copy
cp "$output/.env" "$repo_root/deploy/.env.example"
