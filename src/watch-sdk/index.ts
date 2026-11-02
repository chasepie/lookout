import { chromium, type LaunchOptions, type Page } from 'playwright';

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
  reason: string;
}

export function isFailedRunResult(value: unknown): value is FailedRunResult {
  return typeof value === 'object'
    && value !== null
    && 'success' in value && value.success === false
    && 'reason' in value && typeof value.reason === 'string';
}

export type RunResult = SuccessfulRunResult | FailedRunResult;

export function isRunResult(value: unknown): value is RunResult {
  return isSuccessfulRunResult(value) || isFailedRunResult(value);
}

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
