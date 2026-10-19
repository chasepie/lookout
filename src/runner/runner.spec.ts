import { execFile, spawn, type ChildProcess } from 'node:child_process';
import { once } from 'node:events';
import { mkdtemp, readdir, readFile, rm } from 'node:fs/promises';
import { createServer, type Server } from 'node:http';
import type { AddressInfo } from 'node:net';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { afterAll, beforeAll, describe, expect, test } from 'vitest';
import type { FileKind } from './execute.ts';
import type { ProxySettings } from './protocol.ts';

const runnerPath = fileURLToPath(new URL('./index.ts', import.meta.url));
const fixtureSource = (name: string) =>
  readFile(new URL(`./test/fixtures/${name}`, import.meta.url), 'utf8');

/** Pages the fixture Scripts read, served locally so no test needs the internet */
const pages: Record<string, string> = {
  '/in-stock': '<h1>Widget</h1><p class="price">£20.00</p>',
  '/blocked': '<h1>Access Denied</h1>',
};

const listen = async (server: Server) => {
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  return `http://127.0.0.1:${(server.address() as AddressInfo).port}`;
};

/** Whether the runner has a Script process still running, which it would be its direct child */
const hasChildProcesses = (pid: number) =>
  new Promise<boolean>((resolve) => {
    execFile('pgrep', ['-P', String(pid)], (error) => resolve(error === null));
  });

/** Starts a real runner process and resolves with its base URL once it is listening */
async function startRunner(env: Record<string, string>) {
  const runner = spawn(process.execPath, [runnerPath], {
    env: { ...process.env, PORT: '0', ...env },
    stdio: ['ignore', 'pipe', 'inherit'],
  });
  let output = '';
  for await (const chunk of runner.stdout) {
    output += String(chunk);
    const match = /listening on (http:\/\/\S+)/.exec(output);
    if (match?.[1]) {
      return { runner, url: match[1] };
    }
  }
  throw new Error(`Runner exited before listening: ${output}`);
}

let pageServer: Server;
let pagesUrl: string;
let proxyServer: Server;
let proxyUrl: string;
let runner: ChildProcess;
let runnerUrl: string;
/** The runner's temp directory, where every execution's files must be gone once it responds */
let runnerTmpdir: string;

beforeAll(async () => {
  pageServer = createServer((request, response) => {
    const page = pages[request.url ?? ''];
    response.writeHead(page === undefined ? 404 : 200, { 'content-type': 'text/html; charset=utf-8' });
    response.end(page ?? 'Not found');
  });
  pagesUrl = await listen(pageServer);
  // Answers every request itself, as long as it carries the proxy's credentials
  proxyServer = createServer((request, response) => {
    const expected = `Basic ${Buffer.from('lookout:secret').toString('base64')}`;
    if (request.headers['proxy-authorization'] !== expected) {
      response.writeHead(407, { 'proxy-authenticate': 'Basic realm="proxy"' });
      response.end();
      return;
    }
    response.writeHead(200, { 'content-type': 'text/html; charset=utf-8' });
    response.end(`<h1>Proxied</h1><p class="price">${request.url}</p>`);
  });
  proxyUrl = await listen(proxyServer);
  runnerTmpdir = await mkdtemp(join(tmpdir(), 'lookout-runner-test-'));
  ({ runner, url: runnerUrl } = await startRunner({ HEADLESS: 'true', TMPDIR: runnerTmpdir }));
});

afterAll(async () => {
  runner.kill();
  pageServer.close();
  proxyServer.close();
  await rm(runnerTmpdir, { recursive: true, force: true });
});

interface ExecuteOptions {
  fileKind?: FileKind;
  notificationUrl?: string;
  timeoutMs?: number;
  proxy?: ProxySettings;
}

/** Executes a fixture Script through the runner's HTTP API, separating its duration from the rest of the response */
async function executeTimed(fixture: string, options: ExecuteOptions = {}, url = runnerUrl) {
  const response = await fetch(`${url}/execute`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({
      source: await fixtureSource(fixture),
      fileKind: options.fileKind ?? 'ts',
      context: { notificationUrl: options.notificationUrl ?? `${pagesUrl}/in-stock` },
      timeoutMs: options.timeoutMs ?? 20_000,
      proxy: options.proxy,
    }),
  });
  expect(response.status).toBe(200);
  const { durationMs, ...result } = await response.json() as Record<string, unknown>;
  expect(durationMs).toBeTypeOf('number');
  return { durationMs: durationMs as number, result };
}

const execute = async (fixture: string, options: ExecuteOptions = {}, url = runnerUrl) =>
  (await executeTimed(fixture, options, url)).result;

test('a Script that finds its content Succeeds with that content', { timeout: 30_000 }, async () => {
  const result = await execute('read-price.ts');

  expect(result).toEqual({
    outcome: 'succeeded',
    content: '£20.00',
  });
});

test('a Script that reports an anticipated failure is a Known failure with its details', { timeout: 30_000 }, async () => {
  const result = await execute('read-price.ts', { notificationUrl: `${pagesUrl}/blocked` });

  expect(result).toEqual({
    outcome: 'knownFailure',
    details: 'Access Denied',
  });
});

test.each([
  ['throws', 'throws.ts'],
  ['throws and then breaks cleanup', 'breaks-cleanup-then-throws.ts'],
])('a Script that %s is Errored with its error message', { timeout: 30_000 }, async (_, fixture) => {
  const result = await execute(fixture);

  expect(result).toEqual({
    outcome: 'errored',
    errorMessage: 'The price element has moved',
  });
});

test.each([
  ['nothing', undefined],
  ['a non-boolean success', { success: 'yes' }],
  ['non-string content', { success: true, content: 42 }],
  ['non-string details', { success: false, details: ['Access Denied'] }],
])('a Script that resolves to %s is Errored', { timeout: 30_000 }, async (_, outcome) => {
  const query = outcome === undefined ? '' : `?outcome=${encodeURIComponent(JSON.stringify(outcome))}`;
  const result = await execute('returns-invalid-outcome.ts', {
    notificationUrl: `https://example.com/${query}`,
  });

  expect(result).toEqual({
    outcome: 'errored',
    errorMessage: 'Script must resolve to a ScriptOutcome: { success: boolean, content?: string, details?: string }',
  });
});

test('a Script that default-exports a class runs it', { timeout: 30_000 }, async () => {
  const result = await execute('class-export.ts');

  expect(result).toMatchObject({
    outcome: 'succeeded',
    content: `class ran for ${pagesUrl}/in-stock`,
  });
});

test.each([
  ['a plain function', 'function-export.ts'],
  ['a class without run', 'class-without-run.ts'],
  ['no default export', 'named-export.ts'],
])('a Script with %s is Errored', { timeout: 30_000 }, async (_, fixture) => {
  const result = await execute(fixture);

  expect(result).toMatchObject({
    outcome: 'errored',
    errorMessage: 'Script must default-export a LookoutScript object or a class implementing it',
  });
});

test('a JavaScript Script runs', { timeout: 30_000 }, async () => {
  const result = await execute('read-price.js', { fileKind: 'js' });

  expect(result).toMatchObject({ outcome: 'succeeded', content: '£20.00' });
});

test('a Script can import playwright', { timeout: 30_000 }, async () => {
  const result = await execute('imports-playwright.ts');

  expect(result).toMatchObject({ outcome: 'succeeded', content: 'chromium' });
});

test('a Script that leaves a timer running finishes without waiting for its timeout', { timeout: 10_000 }, async () => {
  const result = await execute('leaves-interval-running.ts', { timeoutMs: 60_000 });

  expect(result).toMatchObject({ outcome: 'succeeded', content: 'done' });
  expect(await hasChildProcesses(runner.pid!)).toBe(false);
});

test("a Script's console output does not affect its outcome", { timeout: 30_000 }, async () => {
  const result = await execute('logs-fake-outcome.ts');

  expect(result).toMatchObject({ outcome: 'succeeded', content: 'done' });
});

test("a malformed result the Script sends on the runner's channel is ignored", { timeout: 30_000 }, async () => {
  const result = await execute('sends-malformed-result.ts');

  expect(result).toEqual({ outcome: 'succeeded', content: 'done' });
});

test("a well-formed result the Script sends on the runner's channel doesn't replace its outcome", { timeout: 30_000 }, async () => {
  const result = await execute('sends-fake-result.ts', { notificationUrl: 'https://example.com/' });

  expect(result).toEqual({ outcome: 'knownFailure', details: 'Access Denied' });
});

test("a well-formed result the Script sends on the runner's channel doesn't replace a timeout", { timeout: 30_000 }, async () => {
  const result = await execute('sends-fake-result.ts', { notificationUrl: 'https://example.com/?hang', timeoutMs: 2_000 });

  expect(result).toEqual({ outcome: 'errored', errorMessage: 'Script timed out after 2000 ms' });
});

test("a Script that wraps the runner's channel can't rewrite its outcome", { timeout: 30_000 }, async () => {
  const result = await execute('wraps-process-send.ts');

  expect(result).toEqual({ outcome: 'knownFailure', details: 'Access Denied' });
});

test('a Script that exits its process is Errored', { timeout: 30_000 }, async () => {
  const result = await execute('exits-process.ts');

  expect(result).toMatchObject({
    outcome: 'errored',
    errorMessage: 'Script process exited with code 3 before reporting an outcome',
  });
});

test("a Script killed at its timeout leaves none of its browser's files behind", { timeout: 30_000 }, async () => {
  const result = await execute('hangs-on-page.ts', { timeoutMs: 2_000 });

  expect(result).toEqual({ outcome: 'errored', errorMessage: 'Script timed out after 2000 ms' });
  expect(await readdir(runnerTmpdir)).toEqual([]);
});

test('a Script that runs past its timeout is Errored', { timeout: 30_000 }, async () => {
  const result = await execute('leaves-interval-running.ts', { timeoutMs: 1 });

  expect(result).toMatchObject({ outcome: 'errored', errorMessage: 'Script timed out after 1 ms' });
});

test("a Script's browser goes through the proxy settings it's given", { timeout: 30_000 }, async () => {
  const result = await execute('read-price.ts', {
    notificationUrl: 'http://shop.lookout.test/widget',
    proxy: { server: proxyUrl, username: 'lookout', password: 'secret' },
  });

  expect(result).toMatchObject({ outcome: 'succeeded', content: 'http://shop.lookout.test/widget' });
});

test('the response reports how long the Script took', { timeout: 30_000 }, async () => {
  const { durationMs, result } = await executeTimed('waits.ts');

  expect(result).toEqual({ outcome: 'succeeded' });
  expect(durationMs).toBeGreaterThanOrEqual(500);
  expect(durationMs).toBeLessThan(20_000);
});

test('a runner configured as headless runs Scripts in a headless browser', { timeout: 30_000 }, async () => {
  const result = await execute('report-user-agent.ts');

  expect(result['content']).toContain('HeadlessChrome');
});

// A visible browser needs a display, which Linux CI machines don't have
describe.skipIf(process.platform === 'linux' && !process.env['DISPLAY'])('a non-headless runner', () => {
  let headedRunner: ChildProcess;
  let headedRunnerUrl: string;

  beforeAll(async () => {
    ({ runner: headedRunner, url: headedRunnerUrl } = await startRunner({ HEADLESS: 'false' }));
  });

  afterAll(() => {
    headedRunner.kill();
  });

  test('runs Scripts in a visible browser', { timeout: 30_000 }, async () => {
    const result = await execute('report-user-agent.ts', {}, headedRunnerUrl);

    expect(result).toMatchObject({ outcome: 'succeeded' });
    expect(result['content']).not.toContain('Headless');
  });
});

const validRequest = {
  source: 'export default { run: async () => ({ success: true }) };',
  fileKind: 'js',
  context: { notificationUrl: 'https://example.com/' },
  timeoutMs: 20_000,
};

test.each([
  ['malformed JSON', '{"source":', 'Request body must be JSON'],
  ['a missing source', { ...validRequest, source: undefined }, 'source must be a string'],
  ['an unknown file kind', { ...validRequest, fileKind: 'py' }, "fileKind must be 'ts' or 'js'"],
  ['a missing Notification URL', { ...validRequest, context: {} }, 'context.notificationUrl must be a string'],
  ['a non-positive timeout', { ...validRequest, timeoutMs: 0 }, 'timeoutMs must be an integer from 1 to 2147483647'],
  ['a timeout too long for a timer', { ...validRequest, timeoutMs: 2_147_483_648 },
    'timeoutMs must be an integer from 1 to 2147483647'],
  ['a proxy without a server', { ...validRequest, proxy: { username: 'lookout' } }, 'proxy.server must be a string'],
  ['a non-string proxy password', { ...validRequest, proxy: { server: 'http://gluetun:8888', password: 1 } },
    'proxy.password must be a string'],
])('a request with %s is rejected', { timeout: 30_000 }, async (_, body, error) => {
  const response = await fetch(`${runnerUrl}/execute`, {
    method: 'POST',
    body: typeof body === 'string' ? body : JSON.stringify(body),
  });

  expect(response.status).toBe(400);
  expect(await response.json()).toEqual({ error });
  expect(await execute('read-price.ts')).toMatchObject({ outcome: 'succeeded' });
});
