import { inspect } from 'node:util';
import { chromium, type Browser, type BrowserContextOptions, type LaunchOptions, type Page } from 'playwright';

interface RunResultBase {
  /**
   * An image of the page to keep with the run, PNG or JPEG, of at most {@link maxScreenshotBytes}, such as
   * `await page.screenshot()`.
   * It takes the place of any screenshot the watch would have Lookout take.
   */
  screenshot?: Uint8Array;
}

/** The script found what it looked for on the page: its content. */
export interface Success extends RunResultBase {
  success: true;
  content: string;
}

export function isSuccess(value: unknown): value is Success {
  return typeof value === 'object'
    && value !== null
    && 'success' in value && value.success === true
    && 'content' in value && typeof value.content === 'string';
}

/** The script recognised a problem, such as a page the VPN region is blocked from, and why. */
export interface KnownFailure extends RunResultBase {
  success: false;
  failureReason: string;
  /**
   * Whether Lookout should fail the VPN over to another region and try the watch again there, for a problem another
   * region could fix, such as a page that blocks this one. Only applies to watches that use the VPN. Leave it out
   * for a problem another region can't fix, such as a sold-out item.
   */
  failover?: boolean;
}

export function isKnownFailure(value: unknown): value is KnownFailure {
  return typeof value === 'object'
    && value !== null
    && 'success' in value && value.success === false
    && 'failureReason' in value && typeof value.failureReason === 'string';
}

export type RunResult = Success | KnownFailure;

export function isRunResult(value: unknown): value is RunResult {
  return isSuccess(value) || isKnownFailure(value);
}

/**
 * Thrown by {@link runWatch} when the watch script crashed: it threw, or returned something other than a
 * {@link RunResult}. What it threw is the `cause`.
 */
export class WatchScriptCrash extends Error {
  /** The full-page screenshot taken when the script crashed, if one was asked for and could be taken. */
  readonly screenshot: Uint8Array | undefined;

  constructor(cause: unknown, screenshot?: Uint8Array) {
    super(cause instanceof Error ? cause.message : describeThrown(cause), { cause });
    this.name = 'WatchScriptCrash';
    this.screenshot = screenshot;
  }

  /**
   * What the script threw: for an error, its stack, which starts with its message, then any `cause` and other
   * properties it has.
   */
  get error(): string {
    return describeThrown(this.cause);
  }
}

/** A thrown value as a person reads it: a string as it is, anything else as Node prints it. */
function describeThrown(value: unknown): string {
  return typeof value === 'string' ? value : inspect(value, { depth: 4 });
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
  /**
   * How a run must end for Lookout to take a full-page screenshot of the page given to the script, when the script
   * returned none of its own.
   */
  screenshotOn?: ScreenshotOptions;
}

/** For each way a run can end, whether to take a full-page screenshot of the page given to the script. */
export interface ScreenshotOptions {
  success?: boolean;
  knownFailure?: boolean;
  /** Only when the script threw: a run that timed out or was cancelled gets none. */
  crash?: boolean;
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
  // Playwright takes `host:port` as an HTTP proxy, and so does this
  const url = new URL(/^[a-z][a-z\d+.-]*:\/\//i.test(proxyUrl) ? proxyUrl : `http://${proxyUrl}`);
  let username: string;
  let password: string;
  try {
    username = decodeURIComponent(url.username);
    password = decodeURIComponent(url.password);
  } catch {
    // The URL holds the password, so it stays out of the message
    throw new Error('The proxy URL\'s username or password has an invalid percent-encoding (a % not followed by two hex digits).');
  }

  return {
    // Not url.origin, which is "null" for schemes such as socks5:
    server: `${url.protocol}//${url.host}`,
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

/** The largest screenshot a run keeps, in bytes: 8 MiB. */
export const maxScreenshotBytes = 8 * 1024 * 1024;

function formatMiB(bytes: number): string {
  return `${(bytes / 1024 / 1024).toFixed(1)} MiB`;
}

// Long enough for a very tall page, short enough that a hung page doesn't hold up the run
const screenshotTimeoutMs = 15_000;

// A headed Chromium, as browserless runs for the Runner, sometimes refuses a capture ("Unable to capture screenshot")
// and allows the next one a moment later
const screenshotAttempts = 3;
const screenshotRetryDelayMs = 250;

/**
 * A full-page screenshot of the page given to the script, or `undefined` if it couldn't be taken, as when the script
 * closed the page. Not being able to take one never changes how the run ended, so the reason only goes to stderr.
 */
async function takeScreenshot(page: Page): Promise<Uint8Array | undefined> {
  const deadline = Date.now() + screenshotTimeoutMs;
  for (let attempt = 1; ; attempt++) {
    try {
      const timeout = Math.max(deadline - Date.now(), 1);
      const screenshot = await page.screenshot({ fullPage: true, type: 'jpeg', quality: 80, timeout });
      if (screenshot.length > maxScreenshotBytes) {
        console.warn(`Left out the screenshot: at ${formatMiB(screenshot.length)} it's over the 8 MiB a run can keep.`);
        return undefined;
      }

      return screenshot;
    } catch (error) {
      if (attempt >= screenshotAttempts || page.isClosed() || Date.now() + screenshotRetryDelayMs >= deadline) {
        console.warn(`Couldn't take a screenshot: ${error instanceof Error ? error.message : String(error)}`);
        return undefined;
      }

      await new Promise(resolve => setTimeout(resolve, screenshotRetryDelayMs));
    }
  }
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
    const screenshotOn = options?.screenshotOn;
    let result: unknown;
    try {
      result = await runScript.run(page, signal);
      if (!isRunResult(result)) {
        throw new Error(`Watch script returned an unexpected result: ${JSON.stringify(result)}`);
      }
      if (!result.success && result.failover !== undefined && typeof result.failover !== 'boolean') {
        throw new Error(`The watch script's failover must be true or false, not ${JSON.stringify(result.failover)}.`);
      }
      if (result.screenshot !== undefined && !(result.screenshot instanceof Uint8Array)) {
        throw new Error('The watch script\'s screenshot must be bytes, such as what page.screenshot() returns, '
          + `not ${result.screenshot === null ? 'null' : typeof result.screenshot}.`);
      }
    } catch (error) {
      // A run that was cancelled ends without a screenshot, so stopping isn't held up by one
      const screenshot = screenshotOn?.crash && !signal.aborted ? await takeScreenshot(page) : undefined;
      throw new WatchScriptCrash(error, screenshot);
    }

    // As with a crash, a run that's being cancelled ends without one, so stopping isn't held up
    const wanted = result.success ? screenshotOn?.success : screenshotOn?.knownFailure;
    if (result.screenshot === undefined && wanted && !signal.aborted) {
      const screenshot = await takeScreenshot(page);
      return screenshot === undefined ? result : { ...result, screenshot };
    }

    return result;
  } finally {
    await browser.close();
  }
}
