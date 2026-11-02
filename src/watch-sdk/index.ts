import { chromium, type LaunchOptions, type Page } from 'playwright';

export interface SuccessfulRunResult {
  success: true;
  content: string;
}

export interface FailedRunResult {
  success: false;
  reason: string;
}

export type RunResult = SuccessfulRunResult | FailedRunResult;

export interface LookoutWatchScript {
  run(page: Page, signal: AbortSignal): Promise<RunResult>;
}

export interface RunWatchOptions {
  launchOptions?: LaunchOptions;
  signal?: AbortSignal;
}

export async function runWatch(runScript: LookoutWatchScript, options?: RunWatchOptions): Promise<RunResult> {
  const signal = options?.signal ?? new AbortController().signal;
  const launchOptions = options?.launchOptions ?? {};

  const browser = await chromium.launch({
    headless: false,
    args: ['--disable-blink-features=AutomationControlled'],
    ...launchOptions,
  });

  try {
    const page = await browser.newPage();
    return await runScript.run(page, signal);
  } finally {
    await browser.close();
  }
}
