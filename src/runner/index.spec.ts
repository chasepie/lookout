import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import type { Server } from 'node:http';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterAll, beforeAll, expect, onTestFinished, test } from 'vitest';
import {
  childPids,
  eventually,
  fixtureRequest,
  hasMarkedProcesses,
  isAlive,
  runnerEnvironment,
  runnerPath,
  scriptSubprocesses,
  servePages,
  startRunner,
  subprocessScript,
  type FixtureOptions,
} from './test/helpers.ts';

let pageServer: Server;
let pagesUrl: string;

beforeAll(async () => {
  ({ server: pageServer, url: pagesUrl } = await servePages());
});

afterAll(() => {
  pageServer.close();
});

/** Asks the runner at `url` to execute a fixture Script through its HTTP API */
const post = async (url: string, fixture: string, options: FixtureOptions) =>
  fetch(`${url}/execute`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(await fixtureRequest(fixture, options)),
  });

test('a runner executes Scripts through its HTTP API', { timeout: 30_000 }, async () => {
  const { runner, url } = await startRunner({ HEADLESS: 'true' });
  try {
    const response = await post(url, 'read-price.ts', { notificationUrl: `${pagesUrl}/in-stock` });

    expect(response.status).toBe(200);
    expect(await response.json()).toMatchObject({ outcome: 'succeeded', content: '£20.00' });
  } finally {
    runner.kill();
  }
});

test.each([
  ['without a host', {}],
  ['given an empty host, as compose passes an unset variable', { RUNNER_HOST: '' }],
])('a runner %s listens only on the loopback address', { timeout: 30_000 }, async (_, env) => {
  const { runner, url } = await startRunner({ HEADLESS: 'true', ...env });
  try {
    // The runner logs the address it bound, not the one it was asked for
    expect(new URL(url).hostname).toBe('127.0.0.1');
  } finally {
    runner.kill();
  }
});

// As in development and the tests, where only this machine can reach the Browserless it starts
test.each(['127.0.0.1', '[::1]', 'localhost'])('a runner whose BROWSERLESS_URL is at %s starts without a token',
  { timeout: 30_000 }, async (host) => {
    const { runner } = await startRunner({ BROWSERLESS_URL: `ws://${host}:3000/chromium/playwright` });
    runner.kill();
  });

// As compose.yaml configures it
test('a runner whose BROWSERLESS_URL other machines can reach starts with a token of its own', { timeout: 30_000 }, async () => {
  const { runner } = await startRunner({
    BROWSERLESS_URL: 'ws://browserless:3000/chromium/playwright?token=browserless-secret',
    RUNNER_TOKEN: 'runner-secret',
  });
  runner.kill();
});

test("a Script's process exits when the runner dies, with the subprocesses it started", { timeout: 30_000 }, async () => {
  const { marker, notificationUrl } = subprocessScript('hang');
  const { runner: doomedRunner, url } = await startRunner({ HEADLESS: 'true' });
  // Even if the test fails before killing it, since a runner left behind keeps the test run from ending
  onTestFinished(() => {
    doomedRunner.kill('SIGKILL');
  });
  const response = post(url, 'starts-subprocess.ts', { notificationUrl, timeoutMs: 60_000 });
  await eventually(() => hasMarkedProcesses(marker));
  const scriptPids = [...await childPids(doomedRunner.pid!), ...await scriptSubprocesses(doomedRunner.pid!)];

  doomedRunner.kill('SIGKILL');

  await expect(response).rejects.toThrow();
  await eventually(async () => scriptPids.every((pid) => !isAlive(pid)) && !await hasMarkedProcesses(marker));
});

test("a runner whose own NODE_OPTIONS preload a file, as a debugger's do, still runs Scripts", { timeout: 30_000 }, async () => {
  // Outside everything a Script's process may read
  const directory = await mkdtemp(join(tmpdir(), 'lookout-preload-'));
  const preloadPath = join(directory, 'preload.cjs');
  await writeFile(preloadPath, '');
  const { runner: debuggedRunner, url } = await startRunner({ HEADLESS: 'true', NODE_OPTIONS: `--require=${preloadPath}` });
  try {
    const response = await post(url, 'read-price.ts', { notificationUrl: `${pagesUrl}/in-stock` });

    expect(await response.json()).toMatchObject({ outcome: 'succeeded', content: '£20.00' });
  } finally {
    debuggedRunner.kill();
    await rm(directory, { recursive: true, force: true });
  }
});

test.each(['SIGINT', 'SIGTERM'] as const)('a runner stopped by %s takes a Script that blocks its process with it', { timeout: 30_000 }, async (signal) => {
  const { runner: stoppedRunner, url } = await startRunner({ HEADLESS: 'true' });
  // Even if the test fails before stopping it, since a runner left behind keeps the test run from ending
  onTestFinished(() => {
    stoppedRunner.kill('SIGKILL');
  });
  const response = post(url, 'blocks-event-loop.ts', { notificationUrl: 'https://example.com/', timeoutMs: 60_000 });
  const browserPids = await scriptSubprocesses(stoppedRunner.pid!);
  const scriptPids = [...await childPids(stoppedRunner.pid!), ...browserPids];
  try {
    stoppedRunner.kill(signal);

    await expect(response).rejects.toThrow();
    await eventually(() => scriptPids.every((pid) => !isAlive(pid)));
  } finally {
    // A Script left spinning would otherwise do so forever
    for (const pid of scriptPids.filter(isAlive)) {
      process.kill(pid, 'SIGKILL');
    }
  }
});

const portError = 'PORT must be an integer from 0 to 65535';
const hostError = 'RUNNER_HOST must be an IP address';
const tokenError = 'RUNNER_TOKEN must be set when RUNNER_HOST is';
const browserlessUrlError = 'BROWSERLESS_URL must be a ws:// or wss:// URL';
const browserlessTokenError = 'BROWSERLESS_URL must have a token when Browserless is at browserless';
const sharedTokenError = "BROWSERLESS_URL's token must differ from RUNNER_TOKEN";

test.each([
  ['a PORT that is empty', { PORT: '' }, portError],
  ['a PORT that is not a number', { PORT: 'http' }, portError],
  ['a PORT that is out of range', { PORT: '70000' }, portError],
  ['a RUNNER_HOST that is a name', { RUNNER_HOST: 'runner.local' }, hostError],
  ['a RUNNER_HOST other machines can reach, and no RUNNER_TOKEN', { RUNNER_HOST: '0.0.0.0' }, tokenError],
  ['a RUNNER_HOST other machines can reach, and an empty RUNNER_TOKEN', { RUNNER_HOST: '::', RUNNER_TOKEN: '' }, tokenError],
  // An address reserved for documentation, which is no machine's
  ['a RUNNER_HOST that is not this machine\'s', { RUNNER_HOST: '192.0.2.1', RUNNER_TOKEN: 'runner-secret' },
    "The runner can't listen on 192.0.2.1"],
  ['a BROWSERLESS_URL that is not a URL', { BROWSERLESS_URL: 'browserless:3000' }, browserlessUrlError],
  ['a BROWSERLESS_URL that is not a WebSocket URL', { BROWSERLESS_URL: 'http://browserless:3000/chromium/playwright' },
    browserlessUrlError],
  ['a BROWSERLESS_URL other machines can reach, without a token', { BROWSERLESS_URL: 'ws://browserless:3000/chromium/playwright' },
    browserlessTokenError],
  ['a BROWSERLESS_URL other machines can reach, with an empty token, as compose passes an unset variable',
    { BROWSERLESS_URL: 'ws://browserless:3000/chromium/playwright?token=' }, browserlessTokenError],
  ['a BROWSERLESS_URL whose token is RUNNER_TOKEN, which Scripts are given',
    { BROWSERLESS_URL: 'ws://browserless:3000/chromium/playwright?token=shared-secret', RUNNER_TOKEN: 'shared-secret' },
    sharedTokenError],
])('a runner with %s refuses to start', { timeout: 30_000 }, async (_, env, error) => {
  const badRunner = spawn(process.execPath, [runnerPath], {
    env: runnerEnvironment(env),
    stdio: ['ignore', 'pipe', 'pipe'],
    // A runner that starts anyway is killed, rather than left listening. One that can't listen first loads Playwright.
    timeout: 20_000,
  });
  let stderr = '';
  badRunner.stderr.on('data', (chunk) => {
    stderr += String(chunk);
  });

  const [code] = await once(badRunner, 'exit') as [number | null];

  expect(code).toBe(1);
  expect(stderr).toContain(error);
});
