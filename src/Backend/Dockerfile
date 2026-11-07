# The Backend, serving the built frontend from wwwroot. To build it alone, from the repo root:
# docker build -f src/Backend/Dockerfile .
# The publish-mode AppHost runs it; docs/security.md explains how it's locked down.

# The latest of this major, like .nvmrc and the Runner's image
ARG NODE_VERSION=26

# The frontend's output is the same on every platform, so it's built natively even when the image is for another one
FROM --platform=$BUILDPLATFORM node:${NODE_VERSION}-bookworm-slim AS frontend
WORKDIR /src
# npm ci checks every workspace against the lockfile
COPY package.json package-lock.json ./
COPY examples/package.json examples/
COPY src/frontend/package.json src/frontend/
COPY src/Runner/package.json src/Runner/
COPY src/watch-sdk/package.json src/watch-sdk/
RUN npm ci --workspace src/frontend --ignore-scripts
COPY src/frontend/ src/frontend/
# Writes to src/Backend/wwwroot, as in development
RUN npm run build -w src/frontend

# Without an apphost, the published app runs on any platform's runtime, so it's built natively too
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
WORKDIR /src
COPY .editorconfig global.json Directory.Build.props Directory.Packages.props ./
COPY src/Core/Core.csproj src/Core/
COPY src/ServiceDefaults/ServiceDefaults.csproj src/ServiceDefaults/
COPY src/Backend/Backend.csproj src/Backend/
RUN dotnet restore src/Backend/Backend.csproj
COPY src/Core/ src/Core/
COPY src/ServiceDefaults/ src/ServiceDefaults/
COPY src/Backend/ src/Backend/
RUN dotnet publish src/Backend/Backend.csproj -c Release --no-restore -p:UseAppHost=false -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=backend /out ./
COPY --from=frontend /src/src/Backend/wwwroot wwwroot/
# Where the AppHost mounts its volumes. A volume starts as a copy of its mount point, owner included, so the Backend's
# user owns the ones it writes; gluetun's is only read.
RUN mkdir scripts database gluetun && chown $APP_UID scripts database
ENV LOOKOUT_WATCH_SCRIPTS_DIR=/app/scripts \
  LOOKOUT_DATABASE_DIR=/app/database
# The non-root user the .NET images provide
USER $APP_UID
ENTRYPOINT ["dotnet", "/app/Backend.dll"]
