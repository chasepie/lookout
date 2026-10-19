import { execFile, spawn, type ChildProcess } from 'node:child_process';
import { once } from 'node:events';
import { mkdtemp, readdir, readFile, rm, writeFile } from 'node:fs/promises';
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

/** The runner's Script processes still running, which are its direct children */
const childPids = (pid: number) =>
  new Promise<number[]>((resolve) => {
    execFile('pgrep', ['-P', String(pid)], (_, stdout) => resolve(stdout.split('\n').filter(Boolean).map(Number)));
  });

const hasChildProcesses = async (pid: number) => (await childPids(pid)).length > 0;

const isAlive = (pid: number) => {
  try {
    process.kill(pid, 0);
    return true;
  } catch {
    return false;
  }
};

/** Polls until `check` holds, failing once `timeoutMs` passes */
async function eventually(check: () => boolean | Promise<boolean>, timeoutMs = 5_000) {
  const deadline = Date.now() + timeoutMs;
  while (!await check()) {
    if (Date.now() > deadline) {
      throw new Error(`Condition still false after ${timeoutMs} ms`);
    }
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
}

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
  await once(runner, 'exit');
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

/**
 * Executes a fixture Script through the runner's HTTP API, separating what every response has (its duration and
 * console output) and its debugging screenshot from the rest of the response
 */
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
  const { durationMs, consoleOutput, screenshot, ...result } = await response.json() as Record<string, unknown>;
  expect(durationMs).toBeTypeOf('number');
  expect(consoleOutput).toBeTypeOf('string');
  return { durationMs: durationMs as number, consoleOutput: consoleOutput as string, screenshot, result };
}

const execute = async (fixture: string, options: ExecuteOptions = {}, url = runnerUrl) =>
  (await executeTimed(fixture, options, url)).result;

const pngSignature = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

const isPng = (screenshot: unknown) =>
  typeof screenshot === 'string' && Buffer.from(screenshot, 'base64').subarray(0, pngSignature.length).equals(pngSignature);

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

test.each([
  ['is a Known failure', 'read-price.ts', '/blocked'],
  ['throws', 'throws.ts', '/in-stock'],
  ['throws from a timer', 'throws-from-timer.ts', '/in-stock'],
  ['resolves to something other than a ScriptOutcome', 'returns-invalid-outcome.ts', '/in-stock'],
])('a Script that %s comes with a screenshot of its page', { timeout: 30_000 }, async (_, fixture, path) => {
  const { result, screenshot } = await executeTimed(fixture, { notificationUrl: `${pagesUrl}${path}` });

  expect(result['outcome']).not.toBe('succeeded');
  expect(isPng(screenshot)).toBe(true);
});

test('a Script that Succeeds comes without a screenshot', { timeout: 30_000 }, async () => {
  const { result, screenshot } = await executeTimed('read-price.ts');

  expect(result).toMatchObject({ outcome: 'succeeded' });
  expect(screenshot).toBeUndefined();
});

test('a Script that fails before it has a page comes without a screenshot', { timeout: 30_000 }, async () => {
  const { result, screenshot } = await executeTimed('named-export.ts');

  expect(result).toMatchObject({ outcome: 'errored' });
  expect(screenshot).toBeUndefined();
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

test("a Script's console output is in the response, and does not affect its outcome", { timeout: 30_000 }, async () => {
  const { consoleOutput, result } = await executeTimed('logs-fake-outcome.ts');

  expect(result).toEqual({ outcome: 'succeeded', content: 'done' });
  // stdout and stderr arrive separately, so their lines needn't be in the order the Script wrote them
  expect(consoleOutput).toContain('{"outcome":"errored","errorMessage":"not from the runner"}\n');
  expect(consoleOutput).toContain('more noise\n');
});

test('a Script that fails straight after logging a lot still has the end of its output', { timeout: 30_000 }, async () => {
  const { consoleOutput, result } = await executeTimed('logs-then-fails-to-load.ts');

  expect(result).toEqual({ outcome: 'errored', errorMessage: 'Failed to load' });
  expect(consoleOutput.startsWith('[Earlier output was cut]\n')).toBe(true);
  expect(consoleOutput.endsWith('LAST-LINE\n')).toBe(true);
});

test("a Script that writes nothing has empty console output, with nothing from Node's own warnings", { timeout: 30_000 }, async () => {
  const { consoleOutput } = await executeTimed('read-price.ts');

  expect(consoleOutput).toBe('');
});

test("a Script's console output is cut down to its last 262,144 characters", { timeout: 30_000 }, async () => {
  const { consoleOutput, result } = await executeTimed('logs-lots.ts');

  expect(result).toEqual({ outcome: 'succeeded' });
  const marker = '[Earlier output was cut]\n';
  expect(consoleOutput.startsWith(marker)).toBe(true);
  expect(consoleOutput.length).toBe(marker.length + 256 * 1024);
  expect(consoleOutput.endsWith(`line 20000 ${'x'.repeat(50)}\n`)).toBe(true);
});

test('an Errored Script has what it logged in the response, with a screenshot of its page', { timeout: 30_000 }, async () => {
  const { consoleOutput, screenshot, result } = await executeTimed('logs-then-throws.ts');

  expect(result).toEqual({ outcome: 'errored', errorMessage: 'The price element has moved' });
  expect(consoleOutput).toBe('Looking for the price\n');
  expect(isPng(screenshot)).toBe(true);
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

test('a wait the Script left pending, which fails as the browser closes, does not affect its outcome', { timeout: 30_000 }, async () => {
  const result = await execute('leaves-wait-unawaited.ts');

  expect(result).toEqual({ outcome: 'succeeded', content: '£20.00' });
});

test.each([
  ['throws from a timer', 'throws-from-timer.ts', 'Thrown from a timer'],
  ['leaves a rejection unhandled', 'rejects-in-background.ts', 'Rejected in the background'],
])('a Script that %s while running is Errored with that error', { timeout: 30_000 }, async (_, fixture, errorMessage) => {
  const result = await execute(fixture);

  expect(result).toEqual({ outcome: 'errored', errorMessage });
});

test.each([
  ['another package', 'imports-other-package.ts', 'https://example.com/', 'vitest'],
  ['a file of its own, once running', 'imports-at-runtime.ts', 'https://example.com/?specifier=./helpers.ts', './helpers.ts'],
])('a Script that imports %s is Errored, saying what it may import', { timeout: 30_000 }, async (_, fixture, notificationUrl, specifier) => {
  const result = await execute(fixture, { notificationUrl });

  expect(result).toEqual({
    outcome: 'errored',
    errorMessage: `Scripts can only import Node built-ins and playwright, not '${specifier}'`,
  });
});

test('a Script can import its types inline from the scripting package', { timeout: 30_000 }, async () => {
  const result = await execute('inline-type-import.ts');

  expect(result).toEqual({ outcome: 'succeeded', content: 'imported' });
});

test('a Script whose process is killed by a signal is Errored, naming the signal', { timeout: 30_000 }, async () => {
  const result = await execute('kills-itself.ts');

  expect(result).toEqual({
    outcome: 'errored',
    errorMessage: 'Script process was killed by SIGKILL before reporting an outcome',
  });
});

test('a Script whose caller disconnects is stopped', { timeout: 30_000 }, async () => {
  const caller = new AbortController();
  const response = fetch(`${runnerUrl}/execute`, {
    method: 'POST',
    body: JSON.stringify({
      source: await fixtureSource('hangs-on-page.ts'),
      fileKind: 'ts',
      context: { notificationUrl: `${pagesUrl}/in-stock` },
      timeoutMs: 60_000,
    }),
    signal: caller.signal,
  });
  await eventually(() => hasChildProcesses(runner.pid!));

  caller.abort();

  await expect(response).rejects.toThrow();
  await eventually(async () => !await hasChildProcesses(runner.pid!));
});

test("a Script's process exits when the runner dies", { timeout: 30_000 }, async () => {
  const { runner: doomedRunner, url } = await startRunner({ HEADLESS: 'true' });
  const response = fetch(`${url}/execute`, {
    method: 'POST',
    body: JSON.stringify({
      source: await fixtureSource('hangs-on-page.ts'),
      fileKind: 'ts',
      context: { notificationUrl: `${pagesUrl}/in-stock` },
      timeoutMs: 60_000,
    }),
  });
  await eventually(() => hasChildProcesses(doomedRunner.pid!));
  const scriptPids = await childPids(doomedRunner.pid!);

  doomedRunner.kill('SIGKILL');

  await expect(response).rejects.toThrow();
  await eventually(() => scriptPids.every((pid) => !isAlive(pid)));
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

test('a Script that runs past its timeout on a page comes with a screenshot of it', { timeout: 30_000 }, async () => {
  const { result, screenshot } = await executeTimed('hangs-on-page.ts', { timeoutMs: 2_000 });

  expect(result).toEqual({ outcome: 'errored', errorMessage: 'Script timed out after 2000 ms' });
  expect(isPng(screenshot)).toBe(true);
});

test('a Script that blocks its process is killed at its timeout, and the runner carries on', { timeout: 30_000 }, async () => {
  const { result, screenshot } = await executeTimed('blocks-event-loop.ts', { timeoutMs: 2_000 });

  expect(result).toEqual({ outcome: 'errored', errorMessage: 'Script timed out after 2000 ms' });
  // It can't take a screenshot when nothing else in its process can run
  expect(screenshot).toBeUndefined();
  expect(await hasChildProcesses(runner.pid!)).toBe(false);
  expect(await execute('read-price.ts')).toMatchObject({ outcome: 'succeeded' });
});

test('a Script that allocates without limit is Errored at its memory limit, and the runner carries on', { timeout: 30_000 }, async () => {
  const { consoleOutput, result } = await executeTimed('allocates-forever.ts');

  expect(result).toEqual({ outcome: 'errored', errorMessage: 'Script exceeded its memory limit of 512 MB' });
  expect(consoleOutput).toContain('JavaScript heap out of memory');
  expect(await execute('read-price.ts')).toMatchObject({ outcome: 'succeeded' });
});

test('a Script whose subprocess holds its output open still ends at its timeout', { timeout: 30_000 }, async () => {
  const { durationMs, result } = await executeTimed('spawns-inheriting-subprocess.ts', { timeoutMs: 1_500 });

  expect(result).toEqual({ outcome: 'errored', errorMessage: 'Script timed out after 1500 ms' });
  // The timeout, the grace period for a screenshot and a little more, but nowhere near the subprocess's 25 s
  expect(durationMs).toBeLessThan(10_000);
});

test("a Script that only writes V8's out-of-memory message isn't taken to have run out of memory", { timeout: 30_000 }, async () => {
  const result = await execute('logs-out-of-memory-then-exits.ts');

  expect(result).toEqual({
    outcome: 'errored',
    errorMessage: 'Script process exited with code 3 before reporting an outcome',
  });
});

test('a Script that runs past its timeout is Errored', { timeout: 30_000 }, async () => {
  const result = await execute('leaves-interval-running.ts', { timeoutMs: 1 });

  expect(result).toMatchObject({ outcome: 'errored', errorMessage: 'Script timed out after 1 ms' });
});

test('a Script can read its own file', { timeout: 30_000 }, async () => {
  const result = await execute('reads-file.ts', { notificationUrl: 'https://example.com/' });

  expect(result).toEqual({ outcome: 'succeeded', content: await fixtureSource('reads-file.ts') });
});

test("a Script reading a file outside its own directory and the runner's packages is Errored", { timeout: 30_000 }, async () => {
  const directory = await mkdtemp(join(tmpdir(), 'lookout-secret-'));
  try {
    const secretPath = join(directory, 'secret.txt');
    await writeFile(secretPath, 'hunter2');

    const result = await execute('reads-file.ts', {
      notificationUrl: `https://example.com/?path=${encodeURIComponent(secretPath)}`,
    });

    expect(result).toEqual({
      outcome: 'errored',
      errorMessage: expect.stringContaining('Access to this API has been restricted') as unknown,
    });
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("a Script's browser goes through the proxy settings it's given", { timeout: 30_000 }, async () => {
  const result = await execute('read-price.ts', {
    notificationUrl: 'http://shop.lookout.test/widget',
    proxy: { server: proxyUrl, username: 'lookout', password: 'secret' },
  });

  expect(result).toMatchObject({ outcome: 'succeeded', content: 'http://shop.lookout.test/widget' });
});

test('the response reports how long the execution took', { timeout: 30_000 }, async () => {
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

test('a request whose proxy is null runs without one', { timeout: 30_000 }, async () => {
  const response = await fetch(`${runnerUrl}/execute`, {
    method: 'POST',
    body: JSON.stringify({ ...validRequest, proxy: null }),
  });

  expect(response.status).toBe(200);
  expect(await response.json()).toMatchObject({ outcome: 'succeeded' });
});

test('a request body over 1 MiB is rejected as too large', { timeout: 30_000 }, async () => {
  const response = await fetch(`${runnerUrl}/execute`, {
    method: 'POST',
    body: JSON.stringify({ ...validRequest, source: `// ${'x'.repeat(1024 * 1024)}` }),
  });

  expect(response.status).toBe(413);
  expect(await response.json()).toEqual({ error: 'Request body must be at most 1048576 bytes' });
  expect(await execute('read-price.ts')).toMatchObject({ outcome: 'succeeded' });
});

test.each([
  ['empty', ''],
  ['not a number', 'http'],
  ['out of range', '70000'],
])('a runner whose PORT is %s refuses to start', { timeout: 30_000 }, async (_, port) => {
  const badRunner = spawn(process.execPath, [runnerPath], {
    env: { ...process.env, PORT: port },
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
  expect(stderr).toContain('PORT must be an integer from 0 to 65535');
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
