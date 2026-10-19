/**
 * The child process that runs one Script. The runner sends it a job over IPC,
 * and it reports the Script's result back over IPC rather than stdout, which the Script shares.
 * The job stays in this module's scope, so the Script can't read its token to forge a report.
 */
import type { LookoutScript, LookoutScriptExport, ScriptOutcome } from '@chasepie/lookout-scripting';
import { once } from 'node:events';
import { registerHooks } from 'node:module';
import { pathToFileURL } from 'node:url';
import { chromium } from 'playwright';
import { isOptionalString, type ScriptJob, type ScriptReport, type ScriptResult } from './protocol.ts';

// The Script's source sits in a temp directory with no node_modules, so resolve playwright from the runner's
registerHooks({
  resolve: (specifier, context, nextResolve) =>
    specifier === 'playwright' || specifier.startsWith('playwright/')
      ? nextResolve(specifier, { ...context, parentURL: import.meta.url })
      : nextResolve(specifier, context),
});

/** The class form of a script's default export, as the scripting contract defines it */
type ScriptClass = Exclude<LookoutScriptExport, LookoutScript>;

/** Whether a default export is a class; what it constructs is checked separately */
const isScriptClass = (value: unknown): value is ScriptClass =>
  typeof value === 'function' && /^class\b/.test(Function.prototype.toString.call(value));

const isLookoutScript = (value: unknown): value is LookoutScript =>
  typeof value === 'object' && value !== null && 'run' in value && typeof value.run === 'function';

/** Loads a script file, whose default export is a LookoutScript object or a class implementing one */
async function loadScript(scriptPath: string): Promise<LookoutScript> {
  const scriptModule = await import(pathToFileURL(scriptPath).href) as { default?: unknown };
  const exported = scriptModule.default;
  const script: unknown = isScriptClass(exported) ? new exported() : exported;

  if (!isLookoutScript(script)) {
    throw new Error('Script must default-export a LookoutScript object or a class implementing it');
  }
  return script;
}

const errored = (error: unknown): ScriptResult => ({
  outcome: 'errored',
  errorMessage: error instanceof Error ? error.message : String(error),
});

async function runJob(job: ScriptJob): Promise<ScriptResult> {
  const script = await loadScript(job.scriptPath);
  const browser = await chromium.launch({
    headless: job.headless,
    args: ['--disable-blink-features=AutomationControlled'],
  });
  try {
    const browserContext = await browser.newContext(job.proxy && { proxy: job.proxy });
    try {
      const page = await browserContext.newPage();
      return toResult(await script.run(page, job.context));
    } catch (error) {
      return errored(error);
    } finally {
      // A failure to clean up must not replace the Script's own result
      await browserContext.close().catch(() => undefined);
    }
  } finally {
    await browser.close().catch(() => undefined);
  }
}

const isScriptOutcome = (value: unknown): value is ScriptOutcome =>
  typeof value === 'object' && value !== null
  && 'success' in value && typeof value.success === 'boolean'
  && isOptionalString('content' in value ? value.content : undefined)
  && isOptionalString('details' in value ? value.details : undefined);

/** Maps what a Script's `run` resolved to onto a Run outcome */
function toResult(outcome: unknown): ScriptResult {
  if (!isScriptOutcome(outcome)) {
    throw new Error('Script must resolve to a ScriptOutcome: { success: boolean, content?: string, details?: string }');
  }
  const { success, content, details } = outcome;
  return {
    outcome: success ? 'succeeded' : 'knownFailure',
    ...(content !== undefined && { content }),
    ...(details !== undefined && { details }),
  };
}

const [job] = await once(process, 'message') as [ScriptJob];
const result = await runJob(job).catch(errored);
// Exit once the result is sent, so a timer or socket the Script left open can't keep the process alive
const report: ScriptReport = { token: job.token, result };
process.send?.(report, () => process.exit());
