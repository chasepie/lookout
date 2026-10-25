import { createServer, type IncomingMessage, type Server } from 'node:http';
import type { Duplex } from 'node:stream';
import { chromium, type BrowserServer } from 'playwright';
import { afterAll, beforeAll, describe, expect, test } from 'vitest';
import type { BrowserSettings } from './browser.ts';
import { createRunner, type Runner } from './execute.ts';
import { executeFixture, hasMarkedProcesses, listen, servePages, subprocessScript } from './test/helpers.ts';

let pageServer: Server;
let pagesUrl: string;

beforeAll(async () => {
  ({ server: pageServer, url: pagesUrl } = await servePages());
});

afterAll(() => {
  pageServer.close();
});

/** Executes a fixture Script with `runner`, reading the in-stock page unless given another */
const executeWith = async (runner: Runner, fixture: string, notificationUrl = `${pagesUrl}/in-stock`) =>
  (await executeFixture(runner.execute, fixture, { notificationUrl })).result;

/** A runner for the tests in the block that calls it, stopped once they are done */
function useRunner(browser: () => BrowserSettings) {
  let runner: Runner | undefined;
  beforeAll(async () => {
    runner = await createRunner(browser());
  });
  afterAll(() => {
    runner?.stopAll();
  });
  return () => runner!;
}

describe('a headless runner', () => {
  const runner = useRunner(() => ({ provider: 'launch', headless: true }));

  test('runs Scripts in a headless browser', { timeout: 30_000 }, async () => {
    const result = await executeWith(runner(), 'report-user-agent.ts');

    expect(result['content']).toContain('HeadlessChrome');
  });
});

// A visible browser needs a display, which Linux CI machines don't have
describe.skipIf(process.platform === 'linux' && !process.env['DISPLAY'])('a non-headless runner', () => {
  const runner = useRunner(() => ({ provider: 'launch', headless: false }));

  test('runs Scripts in a visible browser', { timeout: 30_000 }, async () => {
    const result = await executeWith(runner(), 'report-user-agent.ts');

    expect(result).toMatchObject({ outcome: 'succeeded' });
    expect(result['content']).not.toContain('Headless');
  });
});

// Browserless serves Playwright's protocol, as a browser server Playwright launches does. The stand-in is headless,
// since it ignores the launch options the runner asks for and Linux CI machines have no display.
describe('a runner that connects to a browser server', () => {
  let browserServer: BrowserServer;

  beforeAll(async () => {
    browserServer = await chromium.launchServer({ headless: true });
  });

  afterAll(async () => {
    await browserServer.close();
  });

  const runner = useRunner(() => ({ provider: 'connect', url: browserServer.wsEndpoint() }));

  test('runs Scripts in its browser', { timeout: 30_000 }, async () => {
    const result = await executeWith(runner(), 'read-price.ts');

    expect(result).toEqual({ outcome: 'succeeded', content: '£20.00' });
  });

  test('Errors a Script that starts a process, since its own process needs none', { timeout: 30_000 }, async () => {
    const { marker, notificationUrl } = subprocessScript();

    const result = await executeWith(runner(), 'starts-subprocess.ts', notificationUrl);

    expect(result).toEqual({
      outcome: 'errored',
      errorMessage: expect.stringContaining('Access to this API has been restricted') as unknown,
    });
    expect(await hasMarkedProcesses(marker)).toBe(false);
  });
});

test('a connecting runner asks for a headful Chromium with the flags that hide automation and the home IP', { timeout: 30_000 }, async () => {
  // Stands in for Browserless: records the URL the runner connects to, then refuses the connection
  const requestedUrls: string[] = [];
  const browserless = createServer();
  browserless.on('upgrade', (request: IncomingMessage, socket: Duplex) => {
    requestedUrls.push(request.url ?? '');
    socket.end('HTTP/1.1 403 Forbidden\r\n\r\n');
  });
  const browserlessUrl = (await listen(browserless)).replace(/^http/, 'ws');
  const runner = await createRunner({ provider: 'connect', url: `${browserlessUrl}/chromium/playwright?token=secret` });
  try {
    const result = await executeWith(runner, 'read-price.ts');

    expect(result).toMatchObject({ outcome: 'errored' });
    const requested = new URL(requestedUrls[0] ?? '', browserlessUrl);
    expect(requested.pathname).toBe('/chromium/playwright');
    expect(requested.searchParams.get('token')).toBe('secret');
    expect(JSON.parse(requested.searchParams.get('launch') ?? 'null')).toEqual({
      headless: false,
      args: ['--disable-blink-features=AutomationControlled', '--webrtc-ip-handling-policy=disable_non_proxied_udp'],
    });
  } finally {
    runner.stopAll();
    browserless.close();
  }
});
