import type { Page } from 'playwright';

/**
 * Interface for a Lookout script.
 *
 * A script file default-exports either an object implementing this interface
 * or a class implementing it (see {@link LookoutScriptExport}).
 */
export interface LookoutScript {
  run: (page: Page, context: ScriptContext) => Promise<ScriptOutcome>;
}

/** What a script file default-exports: a Lookout script object, or a class implementing one */
export type LookoutScriptExport = LookoutScript | (new () => LookoutScript);

/** Information about the Watch a script is running for */
export interface ScriptContext {
  /** The page the Watch's notifications link to */
  notificationUrl: string;
}

/** Result of running a Lookout script */
export interface ScriptOutcome {
  success: boolean;
  content?: string;
  details?: string;
}
