/**
 * The protocol between the runner and a Script's child process over IPC: the messages they exchange, and the rules
 * that decide how a Script's ending becomes a Run outcome. Each side creates its own end, given how to send a
 * message, and hands it the messages it receives. Neither side sees the other's rules, or the token that keeps the
 * Script's own use of the channel from being taken for a report.
 */
import type { ScriptContext } from '@chasepie/lookout-scripting';
import type { Serializable } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import type { BrowserSettings, ProxySettings } from './browser.ts';
import { isObject, isOptionalString } from './guards.ts';

/** The Script a child process runs, and how to run it */
export interface ScriptJob {
  scriptPath: string;
  context: ScriptContext;
  browser: BrowserSettings;
  proxy?: ProxySettings;
  /** Whether to screenshot the Script's page even when it Succeeds, as a Test run shows */
  alwaysScreenshot: boolean;
}

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

export const erroredResult = (errorMessage: string): ScriptResult => ({ outcome: 'errored', errorMessage });

/** Whether a Run's screenshot is kept: always when it didn't Succeed, and when it did only if the caller asks */
export const keepsScreenshot = (result: ScriptResult, alwaysScreenshot: boolean) =>
  alwaysScreenshot || result.outcome !== 'succeeded';

/** How long the child gives a screenshot of the Script's page */
export const screenshotTimeoutMs = 2_000;

/**
 * How long the child waits for its console output to be written out before its last report. Long enough for all
 * the output the runner keeps; a bound in case the Script broke the streams.
 */
export const flushTimeoutMs = 1_000;

/** What's left of the grace period once the child has screenshotted and written out its output, for it to report and exit */
const graceMarginMs = 2_000;

/**
 * How long a child process has after its timeout to finish, with its screenshot, before the runner kills it. A
 * child whose Script blocks it can't, and is killed when this runs out.
 */
export const timeoutGraceMs = screenshotTimeoutMs + flushTimeoutMs + graceMarginMs;

/** A job as it goes over IPC, with a secret for this execution that the child's reports must carry */
type JobMessage = ScriptJob & { token: string };

/**
 * What the runner sends a child process whose Script has run past its timeout. The child finishes at once, with a
 * screenshot of the page the Script is stuck on if it can take one, before the runner kills it.
 */
const timedOutMessage = 'timedOut';

/**
 * What a child process reports. The Script shares the process's IPC channel, so the runner accepts only reports
 * carrying the job's token, which the Script isn't given.
 */
type ScriptReport = { token: string } & (
  /**
   * Chromium's process, once launched. Playwright puts it in a process group of its own, outside the one the
   * child leads, so the runner must kill that group as well. A child that connects to Browserless has none.
   */
  | { type: 'browser'; pid: number }
  /** The Script's outcome, sent as soon as it has one, so the screenshot that follows can't make it late */
  | { type: 'outcome'; result: ScriptResult }
  /** The child's last report, sent once its console output is written out, after which it exits */
  | ({ type: 'finished' } & Screenshot)
);

const outcomes: readonly unknown[] = ['succeeded', 'knownFailure', 'errored'] satisfies ScriptResult['outcome'][];

const isScriptResult = (value: unknown): value is ScriptResult =>
  isObject(value)
  && outcomes.includes(value['outcome'])
  && isOptionalString(value['content'])
  && isOptionalString(value['details'])
  && isOptionalString(value['errorMessage']);

/** Whether an IPC message is a report for the job with this token, rather than something the Script sent */
function isReportFor(token: string, message: unknown): message is ScriptReport {
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

/** What V8 writes to stderr as it aborts a process, with SIGABRT, once it has reached its heap limit */
const outOfMemoryMessage = 'JavaScript heap out of memory';

/** How a child process ended, as the runner saw it */
export interface ProcessEnding {
  code: number | null;
  signal: NodeJS.Signals | null;
  /** The end of its stderr, where V8 says it ran out of memory */
  stderr: string;
  /** Why spawning it or sending it a message failed, if either did */
  failure: Error | undefined;
}

/** The runner's end of the protocol with one child process */
export interface RunnerEnd {
  /** Takes a message from the child. Anything that isn't one of its reports, such as something the Script sent, is ignored. */
  receive: (message: unknown) => void;
  /**
   * Tells the child its Script has run past its timeout, so that it finishes at once with a screenshot. A Script
   * that already has an outcome is only finishing up, and that outcome stands. One that comes after this is too late.
   */
  timeOut: () => void;
  /** Chromium's process, once the child has launched one, which leads a process group the runner must also kill */
  readonly browserPid: number | undefined;
  /** The Run outcome, with a screenshot if one is kept, once the child has ended however it did */
  result: (ending: ProcessEnding) => ScriptResult & Screenshot;
}

/**
 * Starts the runner's end of the protocol by sending the child its job. `timeoutMs` and `memoryLimitMb` are the
 * child's limits, for wording an outcome when it ran past one.
 */
export function createRunnerEnd({ job, send, timeoutMs, memoryLimitMb }: {
  job: ScriptJob;
  send: (message: Serializable) => void;
  timeoutMs: number;
  memoryLimitMb: number;
}): RunnerEnd {
  const token = randomUUID();
  let outcome: ScriptResult | undefined;
  let screenshot: Screenshot = {};
  let timedOut = false;
  let browserPid: number | undefined;

  /** Why a process that ended without reporting in time did so */
  const unreportedResult = ({ code, signal, stderr, failure }: ProcessEnding) => {
    if (timedOut) {
      return erroredResult(`Script timed out after ${timeoutMs} ms`);
    }
    // Both must hold, so a Script that only writes V8's message isn't taken to have run out of memory
    if (signal === 'SIGABRT' && stderr.includes(outOfMemoryMessage)) {
      return erroredResult(`Script exceeded its memory limit of ${memoryLimitMb} MB`);
    }
    if (failure) {
      return erroredResult(`Script process failed: ${failure.message}`);
    }
    const ending = signal ? `was killed by ${signal}` : `exited with code ${code}`;
    return erroredResult(`Script process ${ending} before reporting an outcome`);
  };

  send({ ...job, token } satisfies JobMessage);
  return {
    receive: (message) => {
      if (!isReportFor(token, message)) {
        return;
      }
      if (message.type === 'browser') {
        browserPid = message.pid;
      } else if (message.type === 'outcome') {
        // One that comes after the timeout is too late, whatever it is
        if (!timedOut) {
          outcome ??= message.result;
        }
      } else if (message.screenshot !== undefined) {
        screenshot = { screenshot: message.screenshot };
      }
    },
    timeOut: () => {
      if (!outcome) {
        timedOut = true;
        send(timedOutMessage);
      }
    },
    get browserPid() {
      return browserPid;
    },
    result: (ending) => {
      const result = outcome ?? unreportedResult(ending);
      // The child takes a screenshot only when it's kept; this keeps a stray one out of the response all the same
      return { ...result, ...(keepsScreenshot(result, job.alwaysScreenshot) && screenshot) };
    },
  };
}

/** A child process's end of the protocol with the runner */
export interface ChildEnd {
  /** Takes a message from the runner: first the job, then, if its Script runs past its timeout, word of that */
  receive: (message: unknown) => void;
  /** Reports Chromium's process, once launched */
  reportBrowser: (pid: number) => void;
  /** Reports the Script's outcome as soon as it has one. Only the first is reported. */
  reportOutcome: (result: ScriptResult) => void;
  /**
   * Sends the last report, with the screenshot if there is one, then exits, so a timer or socket the Script left
   * open can't keep the process alive. Only the first call does anything.
   */
  finish: (screenshot: Screenshot) => Promise<void>;
}

/**
 * Creates a child process's end of the protocol. `send` must be the IPC channel's from before the Script loaded,
 * so a Script that replaces `process.send` doesn't rewrite the reports by accident. `flush` resolves once the
 * process's console output so far has been written out, and `exit` ends the process.
 */
export function createChildEnd({ send, flush, exit, onJob, onTimedOut }: {
  send: (message: Serializable, sent: () => void) => void;
  flush: () => Promise<void>;
  exit: () => void;
  onJob: (job: ScriptJob) => void;
  onTimedOut: () => void;
}): ChildEnd {
  let token: string | undefined;
  let hasReportedOutcome = false;
  let hasFinished = false;

  const report = (body: DistributiveOmit<ScriptReport, 'token'>, sent: () => void = () => undefined) => {
    if (token === undefined) {
      throw new Error('A child process can only report once it has its job');
    }
    send({ token, ...body } satisfies ScriptReport, sent);
  };

  return {
    // The runner's first message is the job, and a second, if there is one, says the Script has run past its
    // timeout. Both can arrive in one read from the channel, so one listener must take both.
    receive: (message) => {
      if (token === undefined) {
        const { token: jobToken, ...job } = message as JobMessage;
        token = jobToken;
        onJob(job);
      } else if (message === timedOutMessage) {
        onTimedOut();
      }
    },
    reportBrowser: (pid) => report({ type: 'browser', pid }),
    reportOutcome: (result) => {
      if (!hasReportedOutcome) {
        hasReportedOutcome = true;
        report({ type: 'outcome', result });
      }
    },
    finish: async (screenshot) => {
      if (hasFinished) {
        return;
      }
      hasFinished = true;
      // Writes to a pipe are asynchronous, so exiting straight away could lose the end of the Script's output. This
      // comes before the report, since the runner may kill the process once it has it.
      await Promise.race([flush(), new Promise((resolve) => setTimeout(resolve, flushTimeoutMs))]);
      report({ type: 'finished', ...screenshot }, exit);
    },
  };
}

/** `Omit` applied to each member of a union, so the result is still one that narrows on `type` */
type DistributiveOmit<T, K extends PropertyKey> = T extends unknown ? Omit<T, K> : never;
