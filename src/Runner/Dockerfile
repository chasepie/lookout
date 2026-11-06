# The Runner, with Node and the watch CLI it runs scripts with. The AppHost builds and runs it; to build it alone, from
# the repo root: docker build -f src/Runner/Dockerfile .
# docs/security.md explains how it's locked down.

ARG NODE_VERSION=26.13.0

# Node, copied into the runtime image
FROM node:${NODE_VERSION}-bookworm-slim AS node

# The workspace manifests npm ci needs: it checks every workspace against the lockfile
FROM node AS npm-manifests
WORKDIR /src
COPY package.json package-lock.json ./
COPY examples/package.json examples/
COPY src/frontend/package.json src/frontend/
COPY src/Runner/package.json src/Runner/
COPY src/watch-sdk/package.json src/watch-sdk/

# The watch SDK's dist/, which needs its dev dependencies to build
FROM npm-manifests AS watch-sdk
RUN npm ci --workspace src/watch-sdk --ignore-scripts
COPY src/watch-sdk/ src/watch-sdk/
RUN npm run build -w src/watch-sdk

# What the CLI and scripts import when they run: Playwright, and the SDK linked in as a workspace
FROM npm-manifests AS node-modules
RUN npm ci --workspace src/Runner --omit=dev --ignore-scripts

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS runner
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Core/Core.csproj src/Core/
COPY src/ServiceDefaults/ServiceDefaults.csproj src/ServiceDefaults/
COPY src/Runner/Runner.csproj src/Runner/
RUN dotnet restore src/Runner/Runner.csproj
COPY src/Core/ src/Core/
COPY src/ServiceDefaults/ src/ServiceDefaults/
COPY src/Runner/ src/Runner/
RUN dotnet publish src/Runner/Runner.csproj -c Release --no-restore -o /out

# The layout under /app mirrors the repo's, so the SDK's workspace link in node_modules resolves, and scripts mounted
# at /app/scripts import from /app/node_modules
FROM mcr.microsoft.com/dotnet/aspnet:10.0
# Node needs libatomic, which the ASP.NET image doesn't have
RUN apt-get update \
    && apt-get install -y --no-install-recommends libatomic1 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=node /usr/local/bin/node /usr/local/bin/node
WORKDIR /app
COPY --from=node-modules /src/package.json ./
COPY --from=node-modules /src/node_modules node_modules/
COPY --from=watch-sdk /src/src/watch-sdk/package.json src/watch-sdk/
COPY --from=watch-sdk /src/src/watch-sdk/dist src/watch-sdk/dist/
COPY --from=runner /out runner/
ENV LOOKOUT_WATCH_CLI_ROOT=/app \
    LOOKOUT_WATCH_SCRIPTS_DIR=/app/scripts
# The non-root user the .NET images provide
USER $APP_UID
ENTRYPOINT ["dotnet", "/app/runner/Runner.dll"]
