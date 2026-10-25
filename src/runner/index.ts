/**
 * Starts the runner's HTTP API. Configured through the environment:
 * - `PORT`: the port to listen on (default 3000; 0 picks a free one).
 * - `RUNNER_HOST`: the IP address to listen on (default `127.0.0.1`). The runner runs any Script it's sent, so on
 *   any address but a loopback one it refuses to start without `RUNNER_TOKEN`.
 * - `RUNNER_TOKEN`: the bearer token every request must carry, which Lookout is given as `Runner__Token`. Unset or
 *   empty, requests need none.
 * - `BROWSERLESS_URL`: Browserless's Playwright endpoint, such as `ws://browserless:3000/chromium/playwright`, to
 *   connect to for each Script's browser, as in deployment. Without it, each Script launches a local Chromium.
 * - `HEADLESS`: `true` runs the launched Chromium headless, as tests do; otherwise it is visible, for local debugging.
 *   Browserless's is always headful.
 *
 * Both runner settings have their own names, since a shell or another tool may have set `HOST` for its own reasons.
 */
import { BlockList, isIP, type AddressInfo } from 'node:net';
import { createRunner } from './execute.ts';
import { createRunnerServer } from './server.ts';

/** An unset compose variable is passed on as empty, which counts as unset */
const setting = (name: string) => (process.env[name] === '' ? undefined : process.env[name]);

const portText = process.env['PORT'] ?? '3000';
if (!/^\d+$/.test(portText) || Number(portText) > 65_535) {
  console.error(`PORT must be an integer from 0 to 65535, not ${JSON.stringify(portText)}`);
  process.exit(1);
}

const host = setting('RUNNER_HOST') ?? '127.0.0.1';
if (isIP(host) === 0) {
  console.error(`RUNNER_HOST must be an IP address, not ${JSON.stringify(host)}`);
  process.exit(1);
}

const loopback = new BlockList();
loopback.addSubnet('127.0.0.0', 8, 'ipv4');
loopback.addAddress('::1', 'ipv6');

const token = setting('RUNNER_TOKEN');
if (token === undefined && !loopback.check(host, isIP(host) === 6 ? 'ipv6' : 'ipv4')) {
  console.error(`RUNNER_TOKEN must be set when RUNNER_HOST is ${host}, which other machines can reach, since the runner `
    + 'runs any Script it is sent');
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

const server = createRunnerServer(runner.execute, token);

// Such as an address that isn't this machine's, or a port in use
server.once('error', (error) => {
  console.error(`The runner can't listen on ${host} port ${portText}: ${error.message}`);
  process.exit(1);
});

server.listen(Number(portText), host, () => {
  // The address actually bound, rather than the one asked for
  const { address, port } = server.address() as AddressInfo;
  console.log(`Runner listening on http://${isIP(address) === 6 ? `[${address}]` : address}:${port}`);
});
