import type { ScriptContext } from '@chasepie/lookout-scripting';
import { fork, type ChildProcess } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { mkdir, mkdtemp, realpath, rm, writeFile } from 'node:fs/promises';
import { findPackageJSON } from 'node:module';
import { tmpdir } from 'node:os';
import { basename, dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';
import {
  erroredResult,
  isReportFor,
  timedOutMessage,
  type ProxySettings,
  type ScriptJob,
  type ScriptResult,
} from './protocol.ts';

export interface RunnerConfig {
  /** Whether the launched Chromium is headless; non-headless is for local debugging */
  headless: boolean;
}

/** The languages a Script may be written in */
export const fileKinds = ['ts', 'js'] as const;

export type FileKind = typeof fileKinds[number];

/** A request to execute one Script */
export interface ExecuteRequest {
  source: string;
  fileKind: FileKind;
  context: ScriptContext;
  timeoutMs: number;
  /** The proxy a VPN Run's browser goes through; direct Runs have none */
  proxy?: ProxySettings;
}

/** The outcome, with what it took and what the Script wrote to stdout and stderr, for debugging */
export type ExecuteResponse = ScriptResult & { durationMs: number; consoleOutput: string };

const scriptProcessPath = fileURLToPath(new URL('./script-process.ts', import.meta.url));

/** The heap each Script's process gets; one that needs more is Errored rather than taking memory from the others */
const memoryLimitMb = 512;

/** What V8 writes to stderr as it aborts a process, with SIGABRT, once it has reached its heap limit */
const outOfMemoryMessage = 'JavaScript heap out of memory';

/** How much of a Script's stderr to search for that message, which V8 follows with a native stack trace */
const stderrTailLength = 16 * 1024;

/** The runner's own code, which the Script's process runs */
const runnerDirectory = fileURLToPath(new URL('.', import.meta.url));

/** The node_modules the runner's playwright is installed in, which a Script's playwright imports resolve to */
const packagesDirectory = dirname(dirname(findPackageJSON('playwright', import.meta.url)!));

/**
 * Where Playwright installs its browsers, which it reads from to launch Chromium. Only launching needs this, and
 * starting processes; connecting to Browserless (#13) needs neither.
 */
function findBrowsersDirectory() {
  // The executable is nested in a directory for its build, such as chromium-1248, inside the install directory
  for (let directory = chromium.executablePath(); directory !== dirname(directory); directory = dirname(directory)) {
    if (basename(directory).startsWith('chromium-')) {
      return dirname(directory);
    }
  }
  throw new Error(`Can't find Playwright's browsers directory from ${chromium.executablePath()}`);
}

const browsersDirectory = findBrowsersDirectory();

/**
 * Node's permission model for a Script's process. It may read only its own directory, the runner's code and
 * packages, and the browsers, and write only to its temp directory. Starting processes is allowed only because
 * Playwright starts Chromium as one. Node doesn't treat its permission model as a security boundary, and that
 * allowance in particular lets a Script get around the file limits; the runner's container is what contains a
 * Script (ADR-0003).
 */
const permissionArgs = (scriptDirectory: string, tempDirectory: string) => [
  '--permission',
  ...[scriptDirectory, runnerDirectory, packagesDirectory, browsersDirectory].map((path) => `--allow-fs-read=${path}`),
  `--allow-fs-write=${tempDirectory}`,
  '--allow-child-process',
  // Node warns about that allowance on every start, which would end up in every Script's console output
  '--disable-warning=PERM0002',
];

/**
 * How long a Script's process has, once it has run past its timeout, to report a screenshot of its page before it
 * is killed. A process whose Script blocks it can't, and is killed when this runs out.
 */
const timeoutGraceMs = 5_000;

/** How many characters of a Script's console output the runner keeps, so a Script that logs without end can't exhaust it */
const maxConsoleOutputLength = 256 * 1024;

const cutOutputMarker = '[Earlier output was cut]\n';

/**
 * How long to wait for the end of a process's stdout and stderr once it has exited. A subprocess the Script started
 * can hold them open long after, and mustn't hold up the response.
 */
const outputDrainMs = 1_000;

/** Keeps the end of the text appended to it once there's more than `maxLength` characters, marking the cut */
function textTail(maxLength: number) {
  let text = '';
  let cut = false;
  return {
    append: (chunk: string) => {
      text += chunk;
      if (text.length > maxLength) {
        text = text.slice(-maxLength);
        cut = true;
      }
    },
    text: () => (cut ? cutOutputMarker : '') + text,
  };
}

/**
 * Collects a process's stdout and stderr as they arrive, keeping only the end once there's too much. The end is
 * what explains a failure. The end of stderr is kept apart as well, since only there can V8 say the process ran
 * out of memory.
 */
function collectConsoleOutput(child: ChildProcess) {
  const output = textTail(maxConsoleOutputLength);
  const stderr = textTail(stderrTailLength);
  // Decoding as a stream, so a character split across chunks survives
  child.stdout?.setEncoding('utf8').on('data', output.append);
  child.stderr?.setEncoding('utf8').on('data', (chunk: string) => {
    output.append(chunk);
    stderr.append(chunk);
  });
  return { consoleOutput: output.text, stderr: stderr.text };
}

/**
 * Runs a Script's child process to completion, killing it when `signal` aborts or, after a grace period for a
 * screenshot, at the timeout. The process reports its result over IPC, so the Script's console output can't be
 * mistaken for it, and exits after reporting even if the Script left a handle open. Its temp files go in
 * `tempDirectory`, since a killed process can't clean up after itself.
 */
const runScriptProcess = (job: ScriptJob, timeoutMs: number, tempDirectory: string, signal: AbortSignal) =>
  new Promise<ScriptResult & { consoleOutput: string }>((resolve) => {
    const child = fork(scriptProcessPath, {
      execArgv: [`--max-old-space-size=${memoryLimitMb}`, ...permissionArgs(dirname(job.scriptPath), tempDirectory)],
      env: { ...process.env, TMPDIR: tempDirectory },
      stdio: ['ignore', 'pipe', 'pipe', 'ipc'],
      signal,
      killSignal: 'SIGKILL',
    });
    const { consoleOutput, stderr } = collectConsoleOutput(child);
    let reported: ScriptResult | undefined;
    let failure: Error | undefined;
    let timedOut = false;
    let timedOutScreenshot: Pick<ScriptResult, 'screenshot'> = {};
    let killTimer: NodeJS.Timeout | undefined;
    let drainTimer: NodeJS.Timeout | undefined;
    const timer = setTimeout(() => {
      timedOut = true;
      if (child.connected) {
        child.send(timedOutMessage);
      }
      killTimer = setTimeout(() => child.kill('SIGKILL'), timeoutGraceMs);
    }, timeoutMs);

    child.on('message', (message) => {
      if (reported || !isReportFor(job.token, message)) {
        return;
      }
      if (!timedOut) {
        reported = message.result;
      } else {
        // Whatever the Script was doing, it ran past its timeout, so only the screenshot of where it got to counts
        const { screenshot } = message.result;
        timedOutScreenshot = screenshot === undefined ? {} : { screenshot };
        child.kill('SIGKILL');
      }
    });
    // Failing to spawn, to send the job, or being aborted: 'close' still follows, once the process is gone
    child.on('error', (error) => {
      failure ??= error;
      child.kill('SIGKILL');
    });
    /** Why a process that ended without reporting in time did so */
    const unreportedResult = (code: number | null, exitSignal: NodeJS.Signals | null) => {
      if (timedOut) {
        return { ...erroredResult(`Script timed out after ${timeoutMs} ms`), ...timedOutScreenshot };
      }
      if (exitSignal === 'SIGABRT' && stderr().includes(outOfMemoryMessage)) {
        return erroredResult(`Script exceeded its memory limit of ${memoryLimitMb} MB`);
      }
      if (failure) {
        return erroredResult(`Script process failed: ${failure.message}`);
      }
      const ending = exitSignal ? `was killed by ${exitSignal}` : `exited with code ${code}`;
      return erroredResult(`Script process ${ending} before reporting an outcome`);
    };
    child.once('exit', () => {
      clearTimeout(timer);
      clearTimeout(killTimer);
      drainTimer = setTimeout(() => {
        child.stdout?.destroy();
        child.stderr?.destroy();
      }, outputDrainMs);
    });
    // 'close' follows the end of stdout and stderr, so the console output is as complete as it will get
    child.once('close', (code, exitSignal) => {
      clearTimeout(timer);
      clearTimeout(killTimer);
      clearTimeout(drainTimer);
      resolve({ ...(reported ?? unreportedResult(code, exitSignal)), consoleOutput: consoleOutput() });
    });
    child.send(job);
  });

/**
 * Executes a Script in its own child Node process and reports its outcome. Aborting `signal`, as when the caller
 * goes away, stops the Script.
 */
export async function execute(
  request: ExecuteRequest,
  config: RunnerConfig,
  signal: AbortSignal,
): Promise<ExecuteResponse> {
  const started = performance.now();
  // Without symlinks, as the Script's module path is, since Node checks permissions against the path as given
  const directory = await realpath(await mkdtemp(join(tmpdir(), 'lookout-script-')));
  try {
    const scriptPath = join(directory, request.fileKind === 'ts' ? 'script.mts' : 'script.mjs');
    await writeFile(scriptPath, request.source);
    // Where Playwright puts the browser profile and artifacts, so they go when the directory does
    const tempDirectory = join(directory, 'tmp');
    await mkdir(tempDirectory);

    const job: ScriptJob = {
      token: randomUUID(),
      scriptPath,
      context: request.context,
      headless: config.headless,
      ...(request.proxy && { proxy: request.proxy }),
    };
    const result = await runScriptProcess(job, request.timeoutMs, tempDirectory, signal);
    return { ...result, durationMs: Math.round(performance.now() - started) };
  } finally {
    // A killed Script's browser may still be writing its profile as it exits, so retry while it's busy.
    // Failing even so must not replace the Script's result.
    await rm(directory, { recursive: true, force: true, maxRetries: 5 }).catch((error: unknown) => {
      console.error(`Removing ${directory} failed`, error);
    });
  }
}
