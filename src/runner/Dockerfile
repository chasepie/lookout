# The runner image: the runner and playwright, the only package Scripts may import (ADR-0003). It connects to
# Browserless rather than launching a browser, so it has none. Built from the repository root (see compose.yaml).

FROM node:24-slim
ENV NODE_ENV=production
WORKDIR /app
COPY package-lock.json ./
# Only playwright, at the version the lock file gives the runner: its own copy if it needs one other than the
# workspaces' shared one. Installing the runner's workspace would bring other workspaces' packages along.
RUN npm install --omit=dev --ignore-scripts --no-save --no-package-lock "playwright@$(node --print " \
      const { packages } = require('./package-lock.json'); \
      (packages['src/runner/node_modules/playwright'] ?? packages['node_modules/playwright']).version")" \
    && rm package-lock.json \
    && npm cache clean --force
COPY src/runner src/runner
WORKDIR /app/src/runner
USER node
# Lookout reaches it from another container. Only its networks, which reach nothing but Lookout and Browserless
# (compose.yaml), stop anything else sending it a Script.
ENV HOST=0.0.0.0
EXPOSE 3000
CMD ["node", "index.ts"]
