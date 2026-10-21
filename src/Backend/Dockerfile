# The lookout image: the backend, serving the Angular build. Built from the repository root (see compose.yaml).

FROM --platform=$BUILDPLATFORM node:24-slim AS frontend
WORKDIR /src
# npm ci needs every workspace's package.json to match the lock file
COPY package.json package-lock.json tsconfig.json ./
COPY examples/package.json examples/
COPY src/frontend/package.json src/frontend/
COPY src/runner/package.json src/runner/
COPY src/scripting/package.json src/scripting/
RUN npm ci --workspace src/frontend
COPY src/frontend src/frontend
# Builds into src/Backend/wwwroot
RUN npm run build --workspace src/frontend

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
ARG TARGETARCH
WORKDIR /src
COPY .editorconfig global.json Directory.Build.props Directory.Packages.props ./
COPY src/Model/Model.csproj src/Model/
COPY src/Database/Database.csproj src/Database/
COPY src/Backend/Backend.csproj src/Backend/
RUN dotnet restore src/Backend --arch $TARGETARCH
COPY src/Model src/Model
COPY src/Database src/Database
COPY src/Backend src/Backend
COPY examples/bot-detection.ts examples/
COPY --from=frontend /src/src/Backend/wwwroot src/Backend/wwwroot
RUN dotnet publish src/Backend --no-restore --arch $TARGETARCH --configuration Release --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# For the container's health check
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
# The database lives here, on the volume compose.yaml mounts. It's created for the image's non-root user, so a new
# volume starts out owned by it.
ENV Storage__DataDirectory=/data
RUN mkdir /data && chown app:app /data
WORKDIR /app
COPY --from=backend /app .
USER app
EXPOSE 8080
HEALTHCHECK CMD ["curl", "--fail", "--silent", "--show-error", "--output", "/dev/null", "http://localhost:8080/health"]
ENTRYPOINT ["dotnet", "Backend.dll"]
