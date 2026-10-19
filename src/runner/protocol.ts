/** The messages the runner and a Script's child process exchange over IPC */
import type { ScriptContext } from '@chasepie/lookout-scripting';
import type { BrowserContextOptions } from 'playwright';

export type ProxySettings = NonNullable<BrowserContextOptions['proxy']>;

/** What the runner sends a child process: the Script to run and how to run it */
export interface ScriptJob {
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

const outcomes: readonly unknown[] = ['succeeded', 'knownFailure', 'errored'] satisfies ScriptResult['outcome'][];

export const isOptionalString = (value: unknown) => value === undefined || typeof value === 'string';

/**
 * Whether an IPC message is a well-formed result. The Script shares its process's IPC channel,
 * so the runner checks what arrives rather than trusting it.
 */
export const isScriptResult = (message: unknown): message is ScriptResult =>
  typeof message === 'object' && message !== null
  && 'outcome' in message && outcomes.includes(message.outcome)
  && isOptionalString('content' in message ? message.content : undefined)
  && isOptionalString('details' in message ? message.details : undefined)
  && isOptionalString('errorMessage' in message ? message.errorMessage : undefined);
