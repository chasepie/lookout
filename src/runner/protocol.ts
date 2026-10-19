/** The messages the runner and a Script's child process exchange over IPC */
import type { ScriptContext } from '@chasepie/lookout-scripting';
import type { BrowserContextOptions } from 'playwright';

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

/** What a child process reports back: the Run outcome and what goes with it */
export interface ScriptResult {
  outcome: 'succeeded' | 'knownFailure' | 'errored';
  content?: string;
  details?: string;
  errorMessage?: string;
}

/**
 * What a child process sends when its Script has finished. The Script shares the process's IPC channel,
 * so the runner accepts only a report carrying the job's token, which the Script never sees.
 */
export interface ScriptReport {
  token: string;
  result: ScriptResult;
}

const outcomes: readonly unknown[] = ['succeeded', 'knownFailure', 'errored'] satisfies ScriptResult['outcome'][];

export const isOptionalString = (value: unknown) => value === undefined || typeof value === 'string';

const isScriptResult = (value: unknown): value is ScriptResult =>
  typeof value === 'object' && value !== null
  && 'outcome' in value && outcomes.includes(value.outcome)
  && isOptionalString('content' in value ? value.content : undefined)
  && isOptionalString('details' in value ? value.details : undefined)
  && isOptionalString('errorMessage' in value ? value.errorMessage : undefined);

/** Whether an IPC message is the report of the job with this token, rather than something the Script sent */
export const isReportFor = (token: string, message: unknown): message is ScriptReport =>
  typeof message === 'object' && message !== null
  && 'token' in message && message.token === token
  && 'result' in message && isScriptResult(message.result);
