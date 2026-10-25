/**
 * Starts the runner's HTTP API. Configured through the environment:
 * - `PORT`: the port to listen on (default 3000; 0 picks a free one).
 * - `BROWSERLESS_URL`: Browserless's Playwright endpoint, such as `ws://browserless:3000/chromium/playwright`, to
 *   connect to for each Script's browser, as in deployment. Without it, each Script launches a local Chromium.
 * - `HEADLESS`: `true` runs the launched Chromium headless, as tests do; otherwise it is visible, for local debugging.
 *   Browserless's is always headful.
 */
import { createRunner } from './execute.ts';
import { createRunnerServer } from './server.ts';

const portText = process.env['PORT'] ?? '3000';
if (!/^\d+$/.test(portText) || Number(portText) > 65_535) {
  console.error(`PORT must be an integer from 0 to 65535, not ${JSON.stringify(portText)}`);
  process.exit(1);
}

const browserlessUrl = process.env['BROWSERLESS_URL'];
if (browserlessUrl && !['ws:', 'wss:'].includes(URL.parse(browserlessUrl)?.protocol ?? '')) {
  console.error(`BROWSERLESS_URL must be a ws:// or wss:// URL, not ${JSON.stringify(browserlessUrl)}`);
  process.exit(1);
}

const runner = await createRunner(
  browserlessUrl
    ? { provider: 'connect', url: browserlessUrl }
    : { provider: 'launch', headless: process.env['HEADLESS'] === 'true' },
);

// Takes the running Scripts with it, then stops as the signal would have stopped it
for (const signal of ['SIGINT', 'SIGTERM'] as const) {
  process.once(signal, () => {
    runner.stopAll();
    process.kill(process.pid, signal);
  });
}

const server = createRunnerServer(runner.execute);

server.listen(Number(portText), () => {
  const address = server.address();
  const port = typeof address === 'object' && address !== null ? address.port : address;
  console.log(`Runner listening on http://localhost:${port}`);
});
