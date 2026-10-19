/**
 * Starts the runner's HTTP API. Configured through the environment:
 * - `PORT`: the port to listen on (default 3000; 0 picks a free one).
 * - `HEADLESS`: `true` runs the launched Chromium headless, as tests do; otherwise it is visible, for local debugging.
 */
import { stopAllScripts } from './execute.ts';
import { createRunnerServer } from './server.ts';

const portText = process.env['PORT'] ?? '3000';
if (!/^\d+$/.test(portText) || Number(portText) > 65_535) {
  console.error(`PORT must be an integer from 0 to 65535, not ${JSON.stringify(portText)}`);
  process.exit(1);
}

// Takes the running Scripts with it, then stops as the signal would have stopped it
for (const signal of ['SIGINT', 'SIGTERM'] as const) {
  process.once(signal, () => {
    stopAllScripts();
    process.kill(process.pid, signal);
  });
}

const server = createRunnerServer({ headless: process.env['HEADLESS'] === 'true' });

server.listen(Number(portText), () => {
  const address = server.address();
  const port = typeof address === 'object' && address !== null ? address.port : address;
  console.log(`Runner listening on http://localhost:${port}`);
});
