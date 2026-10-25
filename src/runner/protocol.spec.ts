import type { Serializable } from 'node:child_process';
import { afterEach, expect, test, vi } from 'vitest';
import {
  createChildEnd,
  createRunnerEnd,
  flushTimeoutMs,
  type ChildEnd,
  type ProcessEnding,
  type RunnerEnd,
  type ScriptJob,
} from './protocol.ts';

const job: ScriptJob = {
  scriptPath: '/tmp/lookout-script/script.mts',
  context: { notificationUrl: 'https://example.com/product' },
  browser: { provider: 'launch', headless: true },
  alwaysScreenshot: false,
};

const timeoutMs = 20_000;

/** How a process ended that exited without being killed and without failing to start */
const ending = (overrides: Partial<ProcessEnding> = {}): ProcessEnding =>
  ({ code: 0, signal: null, stderr: '', failure: undefined, ...overrides });

/**
 * A runner end and a child end wired to each other in memory, as IPC wires them across processes. Messages are
 * cloned, as IPC serializes them.
 */
function connect(options: { alwaysScreenshot?: boolean; flush?: () => Promise<void> } = {}) {
  const jobs: ScriptJob[] = [];
  const onTimedOut = vi.fn();
  const exit = vi.fn();
  const fromChild: Serializable[] = [];
  const childEnd: ChildEnd = createChildEnd({
    send: (message, sent) => {
      fromChild.push(message);
      runnerEnd.receive(structuredClone(message));
      sent();
    },
    flush: options.flush ?? (() => Promise.resolve()),
    exit,
    onJob: (received) => jobs.push(received),
    onTimedOut,
  });
  // Creating it sends the job, which the child end takes before it sends anything back
  const runnerEnd: RunnerEnd = createRunnerEnd({
    job: { ...job, alwaysScreenshot: options.alwaysScreenshot ?? false },
    send: (message) => childEnd.receive(structuredClone(message)),
    timeoutMs,
    memoryLimitMb: 512,
  });
  return { runnerEnd, childEnd, jobs, onTimedOut, exit, fromChild };
}

/** A runner end on its own, and the token on the job it sent, so a test can send it reports a Script might forge */
function runnerEndAlone() {
  const sent: Serializable[] = [];
  const runnerEnd = createRunnerEnd({ job, send: (message) => sent.push(message), timeoutMs, memoryLimitMb: 512 });
  const { token } = sent[0] as { token: string };
  return { runnerEnd, token, sent };
}

afterEach(() => {
  vi.useRealTimers();
});

test('the child end gets the job, without the token its reports carry', () => {
  const { jobs } = connect();

  expect(jobs).toEqual([job]);
});

test("the Script's outcome becomes the result, and the child end reports only its first", async () => {
  const { runnerEnd, childEnd, fromChild } = connect();

  childEnd.reportOutcome({ outcome: 'succeeded', content: '£20.00' });
  childEnd.reportOutcome({ outcome: 'knownFailure', details: 'Access Denied' });
  await childEnd.finish({});

  expect(fromChild.filter((message) => (message as { type: string }).type === 'outcome')).toHaveLength(1);
  expect(runnerEnd.result(ending())).toEqual({ outcome: 'succeeded', content: '£20.00' });
});

test('the runner end keeps the first outcome it receives', () => {
  const { runnerEnd, token } = runnerEndAlone();

  runnerEnd.receive({ token, type: 'outcome', result: { outcome: 'succeeded', content: 'first' } });
  runnerEnd.receive({ token, type: 'outcome', result: { outcome: 'errored', errorMessage: 'second' } });

  expect(runnerEnd.result(ending())).toEqual({ outcome: 'succeeded', content: 'first' });
});

test('an outcome after the timeout is too late, and the result says the Script timed out', () => {
  const { runnerEnd, childEnd, onTimedOut } = connect();

  runnerEnd.timeOut();
  childEnd.reportOutcome({ outcome: 'succeeded', content: '£20.00' });

  expect(onTimedOut).toHaveBeenCalledOnce();
  expect(runnerEnd.result(ending({ signal: 'SIGKILL' })))
    .toEqual({ outcome: 'errored', errorMessage: `Script timed out after ${timeoutMs} ms` });
});

test('an outcome before the timeout stands, and the child is not told it timed out', async () => {
  const { runnerEnd, childEnd, onTimedOut } = connect();

  childEnd.reportOutcome({ outcome: 'knownFailure', details: 'Access Denied' });
  runnerEnd.timeOut();
  await childEnd.finish({ screenshot: 'cG5n' });

  expect(onTimedOut).not.toHaveBeenCalled();
  expect(runnerEnd.result(ending({ signal: 'SIGKILL' })))
    .toEqual({ outcome: 'knownFailure', details: 'Access Denied', screenshot: 'cG5n' });
});

test.each<[string, (token: string) => unknown]>([
  ['without the job\'s token', () => ({ type: 'outcome', result: { outcome: 'succeeded', content: 'forged' } })],
  ['with another token', () => ({ token: 'not-the-token', type: 'outcome', result: { outcome: 'succeeded' } })],
  ['with a result that is not one', (token) => ({ token, type: 'outcome', result: { outcome: 'passed' } })],
  ['with a content that is not a string',
    (token) => ({ token, type: 'outcome', result: { outcome: 'succeeded', content: 20 } })],
  ['of a type there is no report of', (token) => ({ token, type: 'result', result: { outcome: 'succeeded' } })],
])('the runner end ignores a report %s', (_, report) => {
  const { runnerEnd, token } = runnerEndAlone();

  runnerEnd.receive(report(token));

  expect(runnerEnd.result(ending({ code: 1 })))
    .toEqual({ outcome: 'errored', errorMessage: 'Script process exited with code 1 before reporting an outcome' });
});

test('the runner end ignores a message that is not a report at all', () => {
  const { runnerEnd } = runnerEndAlone();

  for (const message of ['timedOut', 42, null, ['outcome']]) {
    runnerEnd.receive(message);
  }

  expect(runnerEnd.result(ending()).outcome).toBe('errored');
});

test("the runner end learns Chromium's pid from the child end", () => {
  const { runnerEnd, childEnd } = connect();

  childEnd.reportBrowser(4321);

  expect(runnerEnd.browserPid).toBe(4321);
});

test.each([1, 0, -1, 1.5])('the runner end ignores a browser pid of %d, which is no process group to kill', (pid) => {
  const { runnerEnd, token } = runnerEndAlone();

  runnerEnd.receive({ token, type: 'browser', pid });

  expect(runnerEnd.browserPid).toBeUndefined();
});

const outOfMemoryStderr = '\n<--- Last few GCs --->\nFATAL ERROR: Reached heap limit Allocation failed - JavaScript heap out of memory\n';

test.each<[string, Partial<ProcessEnding>, string]>([
  ['aborted with V8\'s message ran out of memory', { signal: 'SIGABRT', stderr: outOfMemoryStderr },
    'Script exceeded its memory limit of 512 MB'],
  ['aborted without V8\'s message was killed', { signal: 'SIGABRT', stderr: 'Aborted' },
    'Script process was killed by SIGABRT before reporting an outcome'],
  ['that only wrote V8\'s message exited', { code: 1, stderr: outOfMemoryStderr },
    'Script process exited with code 1 before reporting an outcome'],
  ['that failed to start failed', { code: -2, failure: new Error('spawn ENOENT') },
    'Script process failed: spawn ENOENT'],
  ['killed by a signal names it', { signal: 'SIGTERM' },
    'Script process was killed by SIGTERM before reporting an outcome'],
  ['that exited names its code', { code: 0 },
    'Script process exited with code 0 before reporting an outcome'],
])('a process without an outcome %s', (_, overrides, errorMessage) => {
  const { runnerEnd } = runnerEndAlone();

  expect(runnerEnd.result(ending(overrides))).toEqual({ outcome: 'errored', errorMessage });
});

test('a timeout outranks how the process ended', () => {
  const { runnerEnd } = runnerEndAlone();

  runnerEnd.timeOut();

  expect(runnerEnd.result(ending({ signal: 'SIGABRT', stderr: outOfMemoryStderr })))
    .toEqual({ outcome: 'errored', errorMessage: `Script timed out after ${timeoutMs} ms` });
});

test.each<[string, boolean, boolean]>([
  ['dropped from a Run that Succeeded', false, false],
  ['kept for a Run that Succeeded when the caller always wants one', true, true],
])('a screenshot is %s', async (_, alwaysScreenshot, kept) => {
  const { runnerEnd, childEnd } = connect({ alwaysScreenshot });

  childEnd.reportOutcome({ outcome: 'succeeded', content: '£20.00' });
  await childEnd.finish({ screenshot: 'cG5n' });

  expect(runnerEnd.result(ending()).screenshot).toBe(kept ? 'cG5n' : undefined);
});

test('a screenshot is kept for a Run that did not Succeed', async () => {
  const { runnerEnd, childEnd } = connect();

  childEnd.reportOutcome({ outcome: 'errored', errorMessage: 'No .price on the page' });
  await childEnd.finish({ screenshot: 'cG5n' });

  expect(runnerEnd.result(ending()).screenshot).toBe('cG5n');
});

test('the child end finishes only once, then exits', async () => {
  const { childEnd, exit, fromChild } = connect();

  await childEnd.finish({ screenshot: 'first' });
  await childEnd.finish({ screenshot: 'second' });

  expect(fromChild).toEqual([expect.objectContaining({ type: 'finished', screenshot: 'first' })]);
  expect(exit).toHaveBeenCalledOnce();
});

test('the child end waits for its console output to be written out before it finishes', async () => {
  let written: () => void = () => undefined;
  const { childEnd, exit, fromChild } = connect({ flush: () => new Promise((resolve) => (written = resolve)) });

  const finishing = childEnd.finish({});
  await Promise.resolve();
  expect(fromChild).toEqual([]);

  written();
  await finishing;
  expect(fromChild).toEqual([expect.objectContaining({ type: 'finished' })]);
  expect(exit).toHaveBeenCalledOnce();
});

test('the child end finishes without its console output once that has taken too long', async () => {
  vi.useFakeTimers();
  const { childEnd, exit, fromChild } = connect({ flush: () => new Promise(() => undefined) });

  const finishing = childEnd.finish({});
  await vi.advanceTimersByTimeAsync(flushTimeoutMs - 1);
  expect(fromChild).toEqual([]);

  await vi.advanceTimersByTimeAsync(1);
  await finishing;
  expect(fromChild).toEqual([expect.objectContaining({ type: 'finished' })]);
  expect(exit).toHaveBeenCalledOnce();
});

test('a job and word of its timeout that arrive together both reach the child', () => {
  const jobs: ScriptJob[] = [];
  const onTimedOut = vi.fn();
  const childEnd = createChildEnd({
    send: () => undefined,
    flush: () => Promise.resolve(),
    exit: () => undefined,
    onJob: (received) => jobs.push(received),
    onTimedOut,
  });
  const fromRunner: Serializable[] = [];
  const runnerEnd = createRunnerEnd({ job, send: (message) => fromRunner.push(message), timeoutMs, memoryLimitMb: 512 });
  runnerEnd.timeOut();

  // As one read from the channel delivers them: one after the other, before anything else runs
  for (const message of fromRunner) {
    childEnd.receive(message);
  }

  expect(jobs).toEqual([job]);
  expect(onTimedOut).toHaveBeenCalledOnce();
});

test('the child end refuses to report before it has its job', () => {
  const childEnd = createChildEnd({
    send: () => undefined,
    flush: () => Promise.resolve(),
    exit: () => undefined,
    onJob: () => undefined,
    onTimedOut: () => undefined,
  });

  expect(() => childEnd.reportOutcome({ outcome: 'succeeded' })).toThrow();
});
