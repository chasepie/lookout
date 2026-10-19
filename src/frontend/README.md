# Lookout frontend

Angular app for creating Watches and running them on demand. See the [repository README](../../README.md) for setup, backend configuration, the API, and tests.

From the repository root:

```sh
npm start -w src/frontend  # development server; proxies /api to localhost:5000
npm run build:frontend    # build into src/Backend/wwwroot
npm run test:e2e           # Playwright against the real backend with a fake runner
```
