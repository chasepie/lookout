import { type Page } from 'playwright';

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
