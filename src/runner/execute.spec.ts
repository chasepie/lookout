import { mkdtemp, readdir, rm, writeFile } from 'node:fs/promises';
import { createServer, type Server } from 'node:http';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterAll, beforeAll, expect, test } from 'vitest';
import { createRunner, type Runner } from './execute.ts';
import {
  eventually,
  executeFixture,
  fixtureRequest,
  fixtureSource,
  hasChildProcesses,
  hasMarkedProcesses,
  isAlive,
  isPng,
  listen,
  scriptSubprocesses,
  servePages,
  subprocessScript,
  type FixtureOptions,
} from './test/helpers.ts';

/** Where this file's own temp files go, outside the runner's */
const systemTmpdir = tmpdir();

let pageServer: Server;
let pagesUrl: string;
let proxyServer: Server;
let proxyUrl: string;
let runner: Runner;
/** The runner's temp directory, where every execution's files must be gone once it responds */
let runnerTmpdir: string;

beforeAll(async () => {
  ({ server: pageServer, url: pagesUrl } = await servePages());
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
  runnerTmpdir = await mkdtemp(join(systemTmpdir, 'lookout-runner-test-'));
  // The runner makes each execution's directory in the temp directory as it is then
  process.env['TMPDIR'] = runnerTmpdir;
  runner = await createRunner({ provider: 'launch', headless: true });
});

afterAll(async () => {
  runner.stopAll();
  process.env['TMPDIR'] = systemTmpdir;
  pageServer.close();
  proxyServer.close();
  await rm(runnerTmpdir, { recursive: true, force: true });
});

const executeTimed = (fixture: string, options: Partial<FixtureOptions> = {}) =>
  executeFixture(runner.execute, fixture, { notificationUrl: `${pagesUrl}/in-stock`, ...options });

const execute = async (fixture: string, options: Partial<FixtureOptions> = {}) =>
  (await executeTimed(fixture, options)).result;

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
  ['closes its browser and then throws', 'closes-browser-then-throws.ts'],
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

test('a Script that Succeeds comes with a screenshot of its page when the caller always wants one', { timeout: 30_000 }, async () => {
  const { result, screenshot } = await executeTimed('read-price.ts', { alwaysScreenshot: true });

  expect(result).toEqual({ outcome: 'succeeded', content: '£20.00' });
  expect(isPng(screenshot)).toBe(true);
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
  expect(await hasChildProcesses(process.pid)).toBe(false);
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

test.each([
  ['a Known failure', 'logs-then-reports-failure.ts', { outcome: 'knownFailure', details: 'Access Denied' }],
  ['an Errored', 'logs-then-throws.ts', { outcome: 'errored', errorMessage: 'The price element has moved' }],
])('%s Script has what it logged in the response, with a screenshot of its page', { timeout: 30_000 }, async (_, fixture, expected) => {
  const { consoleOutput, screenshot, result } = await executeTimed(fixture);

  expect(result).toEqual(expected);
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

test('a Script whose execution is aborted is stopped, with the subprocesses it started', { timeout: 30_000 }, async () => {
  const { marker, notificationUrl } = subprocessScript('hang');
  const caller = new AbortController();
  const response = runner.execute(
    await fixtureRequest('starts-subprocess.ts', { notificationUrl, timeoutMs: 60_000 }),
    caller.signal,
  );
  await eventually(() => hasMarkedProcesses(marker));

  caller.abort();

  expect(await response).toMatchObject({ outcome: 'errored' });
  await eventually(async () => !await hasChildProcesses(process.pid) && !await hasMarkedProcesses(marker));
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

test('an outcome the Script reached before its timeout stands, even when screenshotting it runs past', { timeout: 30_000 }, async () => {
  const result = await execute('hangs-renderer-then-fails.ts', { timeoutMs: 1_500 });

  expect(result).toEqual({ outcome: 'knownFailure', details: 'Access Denied' });
});

test('a Script that runs past its timeout on a page comes with a screenshot of it', { timeout: 30_000 }, async () => {
  const { result, screenshot } = await executeTimed('hangs-on-page.ts', { timeoutMs: 2_000 });

  expect(result).toEqual({ outcome: 'errored', errorMessage: 'Script timed out after 2000 ms' });
  expect(isPng(screenshot)).toBe(true);
});

test('a Script that blocks its process is killed at its timeout with its browser, and the runner carries on', { timeout: 30_000 }, async () => {
  const response = executeTimed('blocks-event-loop.ts', { timeoutMs: 2_000 });
  const browserPids = await scriptSubprocesses(process.pid);

  const { result, screenshot } = await response;

  expect(result).toEqual({ outcome: 'errored', errorMessage: 'Script timed out after 2000 ms' });
  // The runner responds once it has sent SIGKILL, and a busy machine can take a moment to finish off a process
  await expect.poll(() => browserPids.filter(isAlive), { timeout: 5_000 }).toEqual([]);
  // It can't take a screenshot when nothing else in its process can run
  expect(screenshot).toBeUndefined();
  expect(await hasChildProcesses(process.pid)).toBe(false);
  expect(await execute('read-price.ts')).toMatchObject({ outcome: 'succeeded' });
});

test('a Script that allocates without limit is Errored at its memory limit, and the runner carries on', { timeout: 30_000 }, async () => {
  const { consoleOutput, result } = await executeTimed('allocates-forever.ts');

  expect(result).toEqual({ outcome: 'errored', errorMessage: 'Script exceeded its memory limit of 512 MB' });
  expect(consoleOutput).toContain('JavaScript heap out of memory');
  expect(await execute('read-price.ts')).toMatchObject({ outcome: 'succeeded' });
});

test('a Script whose subprocess shares its output still ends at its timeout, and the subprocess with it', { timeout: 30_000 }, async () => {
  const { marker, notificationUrl } = subprocessScript('inherit', 'hang');

  const { durationMs, result } = await executeTimed('starts-subprocess.ts', { notificationUrl, timeoutMs: 1_500 });

  expect(result).toEqual({ outcome: 'errored', errorMessage: 'Script timed out after 1500 ms' });
  // The timeout, the grace period for a screenshot and a little more, but nowhere near the subprocess's 25 s
  expect(durationMs).toBeLessThan(10_000);
  await eventually(async () => !await hasMarkedProcesses(marker));
});

test('a subprocess a Script leaves running is killed when its Run ends', { timeout: 30_000 }, async () => {
  const { notificationUrl } = subprocessScript();

  const result = await execute('starts-subprocess.ts', { notificationUrl });

  expect(result).toMatchObject({ outcome: 'succeeded', content: expect.stringMatching(/^\d+$/) as unknown });
  await eventually(() => !isAlive(Number(result['content'])));
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

test("a Script isn't given the runner's token", { timeout: 30_000 }, async () => {
  process.env['RUNNER_TOKEN'] = 'runner-secret';
  try {
    const result = await execute('reports-environment.ts', { notificationUrl: 'https://example.com/?name=RUNNER_TOKEN' });

    expect(result).toEqual({ outcome: 'succeeded', content: 'unset' });
  } finally {
    delete process.env['RUNNER_TOKEN'];
  }
});

test("a Script reading a file outside its own directory and the runner's packages is Errored", { timeout: 30_000 }, async () => {
  const directory = await mkdtemp(join(systemTmpdir, 'lookout-secret-'));
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
