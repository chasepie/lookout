/** The messages the runner and a Script's child process exchange over IPC */
import type { ScriptContext } from '@chasepie/lookout-scripting';
import type { BrowserContextOptions } from 'playwright';
import { isObject, isOptionalString } from './guards.ts';

export type ProxySettings = NonNullable<BrowserContextOptions['proxy']>;

/** What the runner sends a child process: the Script to run and how to run it */
export interface ScriptJob {
  /** A secret for this execution, which the child's reports must carry; see {@link ScriptReport} */
  token: string;
  scriptPath: string;
  context: ScriptContext;
  headless: boolean;
  proxy?: ProxySettings;
}

/**
 * What the runner sends a child process whose Script has run past its timeout. The child finishes at once, with a
 * screenshot of the page the Script is stuck on if it can take one, before the runner kills it.
 */
export const timedOutMessage = 'timedOut';

/**
 * How long a child process has after its timeout to finish, with its screenshot, before the runner kills it. A
 * child whose Script blocks it can't, and is killed when this runs out.
 */
export const timeoutGraceMs = 5_000;

/** The Run outcome a Script reached, and what goes with it */
export interface ScriptResult {
  outcome: 'succeeded' | 'knownFailure' | 'errored';
  content?: string;
  details?: string;
  errorMessage?: string;
}

/** What helps debug a Run that didn't Succeed */
export interface Screenshot {
  /** A base64 PNG of the Script's page, if it got as far as having one and it could be taken */
  screenshot?: string;
}

/**
 * What a child process reports. The Script shares the process's IPC channel, so the runner accepts only reports
 * carrying the job's token, which the Script isn't given.
 */
export type ScriptReport =
  /**
   * Chromium's process, once launched. Playwright puts it in a process group of its own, outside the one the
   * child leads, so the runner must kill that group as well.
   */
  | { token: string; type: 'browser'; pid: number }
  /** The Script's outcome, sent as soon as it has one, so the screenshot that follows can't make it late */
  | { token: string; type: 'outcome'; result: ScriptResult }
  /** The child's last report, sent once its console output is written out, after which it exits */
  | ({ token: string; type: 'finished' } & Screenshot);

const outcomes: readonly unknown[] = ['succeeded', 'knownFailure', 'errored'] satisfies ScriptResult['outcome'][];

export const erroredResult = (errorMessage: string): ScriptResult => ({ outcome: 'errored', errorMessage });

const isScriptResult = (value: unknown): value is ScriptResult =>
  isObject(value)
  && outcomes.includes(value['outcome'])
  && isOptionalString(value['content'])
  && isOptionalString(value['details'])
  && isOptionalString(value['errorMessage']);

/** Whether an IPC message is a report for the job with this token, rather than something the Script sent */
export function isReportFor(token: string, message: unknown): message is ScriptReport {
  if (!isObject(message) || message['token'] !== token) {
    return false;
  }
  switch (message['type']) {
    case 'browser':
      // Above 1, since killing the group of -1 would signal every process the runner may
      return Number.isInteger(message['pid']) && (message['pid'] as number) > 1;
    case 'outcome':
      return isScriptResult(message['result']);
    case 'finished':
      return isOptionalString(message['screenshot']);
    default:
      return false;
  }
}
