import { chromium, type Browser, type LaunchOptions, type Page } from 'playwright';

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

  const browser = await openBrowser(launchOptions, options?.browserlessUrl);

  try {
    const page = await browser.newPage();
    return await runScript.run(page, signal);
  } finally {
    await browser.close();
  }
}
