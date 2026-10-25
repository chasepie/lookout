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

test('a runner listens only on the loopback address unless given a host', async () => {
  const { runner, url } = await startRunner({ HEADLESS: 'true' });
  try {
    expect(new URL(url).hostname).toBe('127.0.0.1');
  } finally {
    runner.kill();
  }
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
const browserlessUrlError = 'BROWSERLESS_URL must be a ws:// or wss:// URL';

test.each([
  ['a PORT that is empty', { PORT: '' }, portError],
  ['a PORT that is not a number', { PORT: 'http' }, portError],
  ['a PORT that is out of range', { PORT: '70000' }, portError],
  ['a BROWSERLESS_URL that is not a URL', { BROWSERLESS_URL: 'browserless:3000' }, browserlessUrlError],
  ['a BROWSERLESS_URL that is not a WebSocket URL', { BROWSERLESS_URL: 'http://browserless:3000/chromium/playwright' },
    browserlessUrlError],
])('a runner with %s refuses to start', { timeout: 30_000 }, async (_, env, error) => {
  const badRunner = spawn(process.execPath, [runnerPath], {
    env: { ...process.env, PORT: '0', ...env },
    stdio: ['ignore', 'pipe', 'pipe'],
    // A runner that starts anyway is killed, rather than left listening
    timeout: 5_000,
  });
  let stderr = '';
  badRunner.stderr.on('data', (chunk) => {
    stderr += String(chunk);
  });

  const [code] = await once(badRunner, 'exit') as [number | null];

  expect(code).toBe(1);
  expect(stderr).toContain(error);
});
