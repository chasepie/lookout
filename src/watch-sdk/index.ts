import { chromium, type Browser, type BrowserContextOptions, type LaunchOptions, type Page } from 'playwright';

export interface SuccessfulRunResult {
  success: true;
  content: string;
}

export function isSuccessfulRunResult(value: unknown): value is SuccessfulRunResult {
  return typeof value === 'object'
    && value !== null
    && 'success' in value && value.success === true
    && 'content' in value && typeof value.content === 'string';
}

export interface FailedRunResult {
  success: false;
  failureReason: string;
}

export function isFailedRunResult(value: unknown): value is FailedRunResult {
  return typeof value === 'object'
    && value !== null
    && 'success' in value && value.success === false
    && 'failureReason' in value && typeof value.failureReason === 'string';
}

export type RunResult = SuccessfulRunResult | FailedRunResult;

export function isRunResult(value: unknown): value is RunResult {
  return isSuccessfulRunResult(value) || isFailedRunResult(value);
}

export interface LookoutWatchScript {
  run(page: Page, signal: AbortSignal): Promise<RunResult>;
}

export interface RunWatchOptions {
  /**
   * Options for the browser. With {@link browserlessUrl} only `headless` and `args` apply, because
   * browserless launches the browser on its own machine.
   */
  launchOptions?: LaunchOptions;
  /**
   * Base URL of a browserless server, such as `http://localhost:3000`, to run the browser on instead of
   * launching Chromium locally. Query parameters, such as `token`, are passed on to browserless.
   */
  browserlessUrl?: string;
  /**
   * HTTP proxy, such as `http://gluetun:8888`, that the browser sends all its traffic through. The browser
   * connects to it, so with {@link browserlessUrl} it must be reachable from the browserless server. Credentials
   * go in the URL, as in `http://user:password@gluetun:8888`, percent-encoded if they contain reserved characters.
   */
  proxy?: string;
  signal?: AbortSignal;
}

/**
 * Builds the WebSocket URL for Playwright's `chromium.connect()` from a browserless base URL. Browserless
 * launches the browser itself, so the launch options go in its `launch` query parameter.
 */
export function getBrowserlessEndpoint(browserlessUrl: string, launchOptions: Pick<LaunchOptions, 'headless' | 'args'>): string {
  const url = new URL(browserlessUrl);
  if (url.protocol === 'http:' || url.protocol === 'https:') {
    url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:';
  }

  url.pathname = `${url.pathname.replace(/\/+$/, '')}/chromium/playwright`;
  url.searchParams.set('launch', JSON.stringify({ headless: launchOptions.headless, args: launchOptions.args }));
  return url.href;
}

/**
 * Playwright's proxy settings for a proxy URL. Playwright ignores credentials in `server`, so they're passed
 * separately.
 */
export function getProxySettings(proxyUrl: string): NonNullable<BrowserContextOptions['proxy']> {
  const url = new URL(proxyUrl);
  const username = decodeURIComponent(url.username);
  const password = decodeURIComponent(url.password);
  return {
    server: url.origin,
    ...(username === '' ? {} : { username }),
    ...(password === '' ? {} : { password }),
  };
}

/**
 * Runs in every frame of a proxied context. WebRTC sends STUN requests over UDP outside the HTTP proxy, which
 * reveals the real IP, and Chromium's `--force-webrtc-ip-handling-policy` doesn't stop it with a per-context
 * proxy, so pages get no WebRTC at all.
 */
function removeWebRtc(): void {
  for (const name of ['RTCPeerConnection', 'webkitRTCPeerConnection', 'RTCDataChannel', 'RTCIceCandidate', 'RTCSessionDescription']) {
    Reflect.deleteProperty(globalThis, name);
  }
}

async function openBrowser(launchOptions: LaunchOptions, browserlessUrl: string | undefined): Promise<Browser> {
  if (browserlessUrl === undefined) {
    return chromium.launch(launchOptions);
  }

  return chromium.connect(getBrowserlessEndpoint(browserlessUrl, launchOptions));
}

export async function runWatch(runScript: LookoutWatchScript, options?: RunWatchOptions): Promise<RunResult> {
  const signal = options?.signal ?? new AbortController().signal;
  const launchOptions: LaunchOptions = {
    headless: false,
    args: ['--disable-blink-features=AutomationControlled'],
    ...options?.launchOptions,
  };
  const proxy = options?.proxy;

  const browser = await openBrowser(launchOptions, options?.browserlessUrl);

  try {
    const context = await browser.newContext(proxy === undefined ? {} : { proxy: getProxySettings(proxy) });
    if (proxy !== undefined) {
      await context.addInitScript(removeWebRtc);
    }
    const page = await context.newPage();
    return await runScript.run(page, signal);
  } finally {
    await browser.close();
  }
}
