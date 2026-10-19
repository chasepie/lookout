/**
 * Starts the runner's HTTP API. Configured through the environment:
 * - `PORT`: the port to listen on (default 3000).
 * - `HEADLESS`: `true` runs the launched Chromium headless, as tests do; otherwise it is visible, for local debugging.
 */
import { createRunnerServer } from './server.ts';

const server = createRunnerServer({ headless: process.env['HEADLESS'] === 'true' });

server.listen(Number(process.env['PORT'] ?? 3000), () => {
  const address = server.address();
  const port = typeof address === 'object' && address !== null ? address.port : address;
  console.log(`Runner listening on http://localhost:${port}`);
});
