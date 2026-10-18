import type { Page } from 'playwright';

/** Interface for a Lookout script */
export interface LookoutScript {
  run: (page: Page) => Promise<ScriptOutcome>;
}

/** Result of running a Lookout script */
export interface ScriptOutcome {
  success: boolean;
  content?: string;
  details?: string;
}
