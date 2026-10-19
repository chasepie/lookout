/**
 * The child process that runs one Script. The runner sends it a job over IPC,
 * and it reports the Script's result back over IPC rather than stdout, which the Script shares.
 */
import type { LookoutScript, LookoutScriptExport, ScriptOutcome } from '@chasepie/lookout-scripting';
import { isBuiltin, registerHooks } from 'node:module';
import { setTimeout as delay } from 'node:timers/promises';
import { pathToFileURL } from 'node:url';
import { chromium, type Page } from 'playwright';
import { isObject, isOptionalString } from './guards.ts';
import { erroredResult, timedOutMessage, type ScriptJob, type ScriptReport, type ScriptResult } from './protocol.ts';

// Held before the Script loads, so a Script that replaces process.send doesn't rewrite the report by accident.
// Code in this process can still reach the channel some other way: these keep a Script from interfering by
// mistake, they don't contain one, and needn't, since a Script decides its own outcome anyway.
const sendReport = process.send?.bind(process);

// A Script that outlives the runner, such as one that never finishes, would otherwise run forever. This process
// leads a process group, so this takes anything the Script started with it.
process.on('disconnect', () => process.kill(-process.pid, 'SIGKILL'));

/**
 * An error the Script raised outside `run`'s own promise chain, from a timer, an event listener, or a promise it
 * left unhandled. Racing `run` against this makes such an error the Script's outcome, rather than a crash that
 * loses its message. Once `run` has settled, the race is over and later errors, such as a wait the Script left
 * pending failing as the browser closes, are ignored.
 */
const backgroundError = new Promise<never>((_, reject) => {
  process.on('uncaughtException', reject);
  process.on('unhandledRejection', reject);
});

const isPackage = (specifier: string, name: string) => specifier === name || specifier.startsWith(`${name}/`);

/**
 * Limits what the Script at `scriptUrl` may import to Node built-ins and playwright, the only package the runner
 * provides (ADR-0003). The Script's file sits in a temp directory with no node_modules, so its playwright imports
 * resolve from the runner's. The scripting package has only types, so an import from it that survives type
 * stripping, such as `import { type LookoutScript } from …`, gets an empty module. Imports by anything else, such
 * as playwright's own, resolve as usual.
 */
function restrictImports(scriptUrl: string) {
  registerHooks({
    resolve: (specifier, context, nextResolve) => {
      if (context.parentURL !== scriptUrl || isBuiltin(specifier)) {
        return nextResolve(specifier, context);
      }
      if (isPackage(specifier, '@chasepie/lookout-scripting')) {
        return { url: 'data:text/javascript,', shortCircuit: true };
      }
      if (isPackage(specifier, 'playwright')) {
        return nextResolve(specifier, { ...context, parentURL: import.meta.url });
      }
      throw new Error(`Scripts can only import Node built-ins and playwright, not '${specifier}'`);
    },
  });
}

/** The class form of a script's default export, as the scripting contract defines it */
type ScriptClass = Exclude<LookoutScriptExport, LookoutScript>;

/** Whether a default export is a class; what it constructs is checked separately */
const isScriptClass = (value: unknown): value is ScriptClass =>
  typeof value === 'function' && /^class\b/.test(Function.prototype.toString.call(value));

const isLookoutScript = (value: unknown): value is LookoutScript =>
  isObject(value) && typeof value['run'] === 'function';

/** Loads a script file, whose default export is a LookoutScript object or a class implementing one */
async function loadScript(scriptPath: string): Promise<LookoutScript> {
  const scriptUrl = pathToFileURL(scriptPath).href;
  restrictImports(scriptUrl);
  const scriptModule = await import(scriptUrl) as { default?: unknown };
  const exported = scriptModule.default;
  const script: unknown = isScriptClass(exported) ? new exported() : exported;

  if (!isLookoutScript(script)) {
    throw new Error('Script must default-export a LookoutScript object or a class implementing it');
  }
  return script;
}

const errored = (error: unknown) => erroredResult(error instanceof Error ? error.message : String(error));

/** Short enough to leave time to report within the runner's grace period, long beyond what a screenshot needs */
const screenshotTimeoutMs = 3_000;

/**
 * A screenshot of the Script's page, to tell an "Access Denied" page from a changed selector. There's none if the
 * page can't take one, such as when the Script has closed it.
 */
async function screenshotOf(page: Page): Promise<Pick<ScriptResult, 'screenshot'>> {
  try {
    return { screenshot: (await page.screenshot({ timeout: screenshotTimeoutMs })).toString('base64') };
  } catch {
    return {};
  }
}

/** The Script's page, once it has one, so there's something to screenshot if it runs past its timeout */
let scriptPage: Page | undefined;

async function runJob(job: ScriptJob): Promise<ScriptResult> {
  const script = await Promise.race([loadScript(job.scriptPath), backgroundError]);
  const browser = await chromium.launch({
    headless: job.headless,
    args: ['--disable-blink-features=AutomationControlled'],
  });
  try {
    const browserContext = await browser.newContext(job.proxy && { proxy: job.proxy });
    const page = await browserContext.newPage();
    scriptPage = page;
    // Promise.try, so that a run that throws rather than rejecting is Errored with a screenshot too
    const running = Promise.try(() => script.run(page, job.context));
    const result = await Promise.race([running, backgroundError]).then(toResult).catch(errored);
    return result.outcome === 'succeeded' ? result : { ...result, ...await screenshotOf(page) };
  } finally {
    // Closing the browser closes its contexts. A failure to clean up must not replace the Script's own result
    await browser.close().catch(() => undefined);
  }
}

const isScriptOutcome = (value: unknown): value is ScriptOutcome =>
  isObject(value)
  && typeof value['success'] === 'boolean'
  && isOptionalString(value['content'])
  && isOptionalString(value['details']);

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

/** Long enough to write out all the console output the runner keeps; a bound in case the Script broke the streams */
const flushTimeoutMs = 2_000;

/** Resolves once everything written to `stream` so far has been handed on */
const flushed = (stream: NodeJS.WriteStream) =>
  new Promise<void>((resolve) => {
    stream.write('', () => resolve());
  });

let hasReported = false;

/** Reports a result, only once, then exits, so a timer or socket the Script left open can't keep the process alive */
async function report(token: string, result: ScriptResult) {
  if (hasReported) {
    return;
  }
  hasReported = true;
  // Writes to a pipe are asynchronous, so exiting straight away could lose the end of the Script's output. This
  // comes before the report, since the runner kills a process that reports after its timeout.
  await Promise.race([Promise.all([flushed(process.stdout), flushed(process.stderr)]), delay(flushTimeoutMs)]);
  const message: ScriptReport = { token, result };
  sendReport?.(message, () => process.exit());
}

let jobToken: string | undefined;

// The runner's first message is the job, and a second, if there is one, says the Script has run past its timeout.
// Both can arrive in one read from the channel, so a listener added once the job has arrived could miss the second.
process.on('message', (message: unknown) => {
  if (jobToken === undefined) {
    const job = message as ScriptJob;
    jobToken = job.token;
    void runJob(job).catch(errored).then((result) => report(job.token, result));
  } else if (message === timedOutMessage) {
    // The runner words the timeout itself; all it needs from here is the screenshot
    const token = jobToken;
    void (scriptPage ? screenshotOf(scriptPage) : Promise.resolve({}))
      .then((screenshot) => report(token, { outcome: 'errored', ...screenshot }));
  }
});
