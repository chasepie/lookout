/** The messages the runner and a Script's child process exchange over IPC */
import type { ScriptContext } from '@chasepie/lookout-scripting';
import type { BrowserContextOptions } from 'playwright';
import { isObject, isOptionalString } from './guards.ts';

export type ProxySettings = NonNullable<BrowserContextOptions['proxy']>;

/** What the runner sends a child process: the Script to run and how to run it */
export interface ScriptJob {
  /** A secret for this execution, which the child's report must carry; see {@link ScriptReport} */
  token: string;
  scriptPath: string;
  context: ScriptContext;
  headless: boolean;
  proxy?: ProxySettings;
}

/**
 * What the runner sends a child process whose Script has run past its timeout. The child reports at once with a
 * screenshot of the page the Script is stuck on, if it can, before the runner kills it.
 */
export const timedOutMessage = 'timedOut';

/** What a child process reports back: the Run outcome and what goes with it */
export interface ScriptResult {
  outcome: 'succeeded' | 'knownFailure' | 'errored';
  content?: string;
  details?: string;
  errorMessage?: string;
  /** A base64 PNG of the Script's page, for a Run that didn't Succeed */
  screenshot?: string;
}

/**
 * What a child process sends when its Script has finished. The Script shares the process's IPC channel,
 * so the runner accepts only a report carrying the job's token, which the Script isn't given.
 */
export interface ScriptReport {
  token: string;
  result: ScriptResult;
}

const outcomes: readonly unknown[] = ['succeeded', 'knownFailure', 'errored'] satisfies ScriptResult['outcome'][];

export const erroredResult = (errorMessage: string): ScriptResult => ({ outcome: 'errored', errorMessage });

const isScriptResult = (value: unknown): value is ScriptResult =>
  isObject(value)
  && outcomes.includes(value['outcome'])
  && isOptionalString(value['content'])
  && isOptionalString(value['details'])
  && isOptionalString(value['errorMessage'])
  && isOptionalString(value['screenshot']);

/** Whether an IPC message is the report of the job with this token, rather than something the Script sent */
export const isReportFor = (token: string, message: unknown): message is ScriptReport =>
  isObject(message) && message['token'] === token && isScriptResult(message['result']);
