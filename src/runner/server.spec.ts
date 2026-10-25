import { once } from 'node:events';
import type { Server } from 'node:http';
import type { AddressInfo } from 'node:net';
import { afterAll, beforeAll, beforeEach, expect, test, vi } from 'vitest';
import type { ExecuteRequest, ExecuteResponse, ExecuteScript } from './execute.ts';
import { createRunnerServer } from './server.ts';

const succeeded: ExecuteResponse = { outcome: 'succeeded', content: '£20.00', durationMs: 1432, consoleOutput: '' };

/** What the server asked the fake runner to execute */
let executed: ExecuteRequest[] = [];

/** What the fake runner does when asked to execute */
let fakeExecute: ExecuteScript;

let server: Server;
let serverUrl: string;

beforeAll(async () => {
  server = createRunnerServer((request, signal) => {
    executed.push(request);
    return fakeExecute(request, signal);
  });
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  serverUrl = `http://127.0.0.1:${(server.address() as AddressInfo).port}`;
});

afterAll(() => {
  server.close();
});

beforeEach(() => {
  executed = [];
  fakeExecute = () => Promise.resolve(succeeded);
});

const validRequest = {
  source: 'export default { run: async () => ({ success: true }) };',
  fileKind: 'js',
  context: { notificationUrl: 'https://example.com/' },
  timeoutMs: 20_000,
};

const post = (body: unknown, init: RequestInit = { headers: { 'content-type': 'application/json' } }) =>
  fetch(`${serverUrl}/execute`, {
    method: 'POST',
    body: typeof body === 'string' ? body : JSON.stringify(body),
    ...init,
  });

test('a request is executed as given, and the response is what the runner reported', async () => {
  const response = await post(validRequest);

  expect(response.status).toBe(200);
  expect(await response.json()).toEqual(succeeded);
  expect(executed).toEqual([{ ...validRequest, alwaysScreenshot: false }]);
});

test("a request's proxy and alwaysScreenshot are passed on", async () => {
  const proxy = { server: 'http://gluetun:8888', username: 'lookout', password: 'secret', bypass: 'localhost' };

  await post({ ...validRequest, proxy, alwaysScreenshot: true });

  expect(executed).toEqual([{ ...validRequest, proxy, alwaysScreenshot: true }]);
});

test('a request whose proxy is null is executed without one', async () => {
  const response = await post({ ...validRequest, proxy: null });

  expect(response.status).toBe(200);
  expect(executed[0]).not.toHaveProperty('proxy');
});

test('a request whose alwaysScreenshot is null is executed as if it were left out', async () => {
  const response = await post({ ...validRequest, alwaysScreenshot: null });

  expect(response.status).toBe(200);
  expect(executed[0]?.alwaysScreenshot).toBe(false);
});

test.each([
  ['malformed JSON', '{"source":', 'Request body must be JSON'],
  ['a body that is not an object', '42', 'Request body must be a JSON object'],
  ['a missing source', { ...validRequest, source: undefined }, 'source must be a string'],
  ['an unknown file kind', { ...validRequest, fileKind: 'py' }, "fileKind must be 'ts' or 'js'"],
  ['a missing Notification URL', { ...validRequest, context: {} }, 'context.notificationUrl must be a string'],
  ['a non-positive timeout', { ...validRequest, timeoutMs: 0 }, 'timeoutMs must be an integer from 1 to 2147483647'],
  ['a timeout too long for a timer', { ...validRequest, timeoutMs: 2_147_483_648 },
    'timeoutMs must be an integer from 1 to 2147483647'],
  ['a proxy that is not an object', { ...validRequest, proxy: 'http://gluetun:8888' }, 'proxy must be an object'],
  ['a proxy without a server', { ...validRequest, proxy: { username: 'lookout' } }, 'proxy.server must be a string'],
  ['a non-string proxy password', { ...validRequest, proxy: { server: 'http://gluetun:8888', password: 1 } },
    'proxy.password must be a string'],
  ['a non-boolean alwaysScreenshot', { ...validRequest, alwaysScreenshot: 'yes' }, 'alwaysScreenshot must be a boolean'],
])('a request with %s is rejected, without executing anything', async (_, body, error) => {
  const response = await post(body);

  expect(response.status).toBe(400);
  expect(await response.json()).toEqual({ error });
  expect(executed).toEqual([]);
});

test.each([
  ['no Content-Type', undefined],
  ['a text Content-Type', 'text/plain;charset=UTF-8'],
])('a request with %s is refused, so a web page can only send one after a CORS preflight', async (_, contentType) => {
  const response = await fetch(`${serverUrl}/execute`, {
    method: 'POST',
    // fetch gives a string body text/plain unless told otherwise, as a page's no-cors request would have
    body: contentType === undefined ? new Blob([JSON.stringify(validRequest)]) : JSON.stringify(validRequest),
    ...(contentType !== undefined && { headers: { 'content-type': contentType } }),
  });

  expect(response.status).toBe(415);
  expect(await response.json()).toEqual({ error: 'Content-Type must be application/json' });
  expect(executed).toEqual([]);
});

test('a request from a web page is refused, even one the page counts as same-origin', async () => {
  // As a page on a domain rebound to the runner's address would send
  const response = await post(validRequest, {
    headers: { 'content-type': 'application/json', origin: 'http://rebound.example' },
  });

  expect(response.status).toBe(403);
  expect(await response.json()).toEqual({ error: 'Requests from web pages are refused' });
  expect(executed).toEqual([]);
});

test('given a token, a request is refused unless it carries that token, and executed when it does', async () => {
  const guarded = createRunnerServer((request, signal) => {
    executed.push(request);
    return fakeExecute(request, signal);
  }, 'runner-secret');
  guarded.listen(0, '127.0.0.1');
  await once(guarded, 'listening');
  try {
    const postWith = (headers: Record<string, string>) =>
      fetch(`http://127.0.0.1:${(guarded.address() as AddressInfo).port}/execute`, {
        method: 'POST',
        headers: { 'content-type': 'application/json', ...headers },
        body: JSON.stringify(validRequest),
      });

    const missing = await postWith({});
    const wrong = await postWith({ authorization: 'Bearer runner-secreT' });
    const prefixed = await postWith({ authorization: 'Bearer runner-secret-and-more' });

    for (const response of [missing, wrong, prefixed]) {
      expect(response.status).toBe(401);
      expect(await response.json()).toEqual({ error: 'Requests without the runner token are refused' });
    }
    expect(executed).toEqual([]);

    const carried = await postWith({ authorization: 'Bearer runner-secret' });

    expect(carried.status).toBe(200);
    expect(executed).toHaveLength(1);
  } finally {
    guarded.close();
  }
});

test('a request whose Content-Type has parameters is accepted', async () => {
  const response = await post(validRequest, { headers: { 'content-type': 'Application/JSON; charset=utf-8' } });

  expect(response.status).toBe(200);
});

test('a request body over 1 MiB is rejected as too large, and the next request is still served', async () => {
  const response = await post({ ...validRequest, source: `// ${'x'.repeat(1024 * 1024)}` });

  expect(response.status).toBe(413);
  expect(await response.json()).toEqual({ error: 'Request body must be at most 1048576 bytes' });
  expect((await post(validRequest)).status).toBe(200);
  expect(executed).toHaveLength(1);
});

test.each([
  ['GET', '/execute'],
  ['POST', '/run'],
])('a %s to %s is not found', async (method, path) => {
  const response = await fetch(`${serverUrl}${path}`, { method });

  expect(response.status).toBe(404);
  expect(await response.json()).toEqual({ error: 'Not found' });
});

test('a failure inside the runner is an internal error, without its details', async () => {
  const logged = vi.spyOn(console, 'error').mockImplementation(() => undefined);
  fakeExecute = () => Promise.reject(new Error('Disk full at /tmp/lookout-script-x'));

  const response = await post(validRequest);

  expect(response.status).toBe(500);
  expect(await response.json()).toEqual({ error: 'Internal runner error' });
  expect(logged).toHaveBeenCalledOnce();
  logged.mockRestore();
});

test('a caller that goes away stops the execution', async () => {
  const { promise: started, resolve: start } = Promise.withResolvers<void>();
  const { promise: stopped, resolve: stop } = Promise.withResolvers<void>();
  fakeExecute = (_, signal) => {
    signal.addEventListener('abort', () => stop());
    start();
    return stopped.then(() => succeeded);
  };
  const caller = new AbortController();

  const response = post(validRequest, { headers: { 'content-type': 'application/json' }, signal: caller.signal });
  await started;
  caller.abort();

  await expect(response).rejects.toThrow();
  await stopped;
});
