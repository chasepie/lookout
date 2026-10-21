import type { ScriptContext } from '@chasepie/lookout-scripting';
import { fork, type ChildProcess } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { mkdir, mkdtemp, realpath, rm, writeFile } from 'node:fs/promises';
import { findPackageJSON } from 'node:module';
import { tmpdir } from 'node:os';
import { basename, dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  erroredResult,
  isReportFor,
  timedOutMessage,
  timeoutGraceMs,
  type BrowserSettings,
  type ProxySettings,
  type Screenshot,
  type ScriptJob,
  type ScriptResult,
} from './protocol.ts';

export interface RunnerConfig {
  browser: BrowserSettings;
  /** What a Script's process may do on top of what every one may, for its browser; see {@link createRunnerConfig} */
  browserPermissionArgs: string[];
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

/** What a Script's process left to debug it by: what it wrote to stdout and stderr and, unless it Succeeded, a screenshot */
type DebugOutput = Screenshot & { consoleOutput: string };

/** The outcome, with how long the execution took and what to debug it by */
export type ExecuteResponse = ScriptResult & DebugOutput & { durationMs: number };

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

/** Where Playwright installs its browsers, which it reads from to launch Chromium */
async function findBrowsersDirectory() {
  // Imported only here, since a runner that connects to Browserless needs none of it
  const { chromium } = await import('playwright');
  // The executable is nested in a directory for its build, such as chromium-1248, inside the install directory
  for (let directory = chromium.executablePath(); directory !== dirname(directory); directory = dirname(directory)) {
    if (basename(directory).startsWith('chromium-')) {
      return dirname(directory);
    }
  }
  throw new Error(`Can't find Playwright's browsers directory from ${chromium.executablePath()}`);
}

/**
 * System files Playwright reads on Linux as it's imported: the signs of running under WSL. These, and the one it
 * reads to launch Chromium, are granted even where they don't exist, since under the permission model even checking
 * whether a file exists throws for a path that isn't granted.
 */
const playwrightSystemFiles = ['/proc/version', '/proc/sys/fs/binfmt_misc/WSLInterop', '/run/WSL'];

/** The OS's name, which Playwright reads on Linux to tell which build of Chromium to launch */
const osReleaseFile = '/etc/os-release';

/**
 * The runner's configuration for getting browsers from `browser`. To launch Chromium, a Script's process needs to
 * read the browsers and the OS's name, and to start processes, since Chromium is one. That allowance lets a Script
 * get around the file limits, and start a process that outlives it, so a process that connects to Browserless
 * doesn't get it. Worked out as the runner starts, so an install it can't launch from stops it there.
 */
export async function createRunnerConfig(browser: BrowserSettings): Promise<RunnerConfig> {
  if (browser.provider === 'connect') {
    return { browser, browserPermissionArgs: [] };
  }
  const browsersDirectory = await findBrowsersDirectory();
  return {
    browser,
    browserPermissionArgs: [
      ...[browsersDirectory, osReleaseFile].map((path) => `--allow-fs-read=${path}`),
      '--allow-child-process',
      // Node warns about that allowance on every start, which would end up in every Script's console output
      '--disable-warning=PERM0002',
    ],
  };
}

/**
 * Node's permission model for a Script's process. It may read only its own directory, the runner's code and
 * packages and the system files Playwright needs, and write only to its temp directory, with what its browser needs
 * on top. Node doesn't treat its permission model as a security boundary; the runner's container is what contains a
 * Script (ADR-0003).
 */
const permissionArgs = (config: RunnerConfig, scriptDirectory: string, tempDirectory: string) => [
  '--permission',
  ...[scriptDirectory, runnerDirectory, packagesDirectory, ...playwrightSystemFiles]
    .map((path) => `--allow-fs-read=${path}`),
  `--allow-fs-write=${tempDirectory}`,
  ...config.browserPermissionArgs,
];

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
      // Trimming only once there's twice too much, so heavy output isn't copied on every chunk
      if (text.length > 2 * maxLength) {
        text = text.slice(-maxLength);
        cut = true;
      }
    },
    text: () => {
      if (!cut && text.length <= maxLength) {
        return text;
      }
      const kept = text.slice(-maxLength);
      // Without the second half of a character the cut went through, which on its own isn't valid text
      return cutOutputMarker + (/^[\uDC00-\uDFFF]/.test(kept) ? kept.slice(1) : kept);
    },
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
 * The runner's environment for a Script's process, with its temp files in `tempDirectory`. NODE_OPTIONS is left
 * out: what it preloads, such as a debugger's bootloader, is the runner's, and the Script's process may not read it.
 */
function scriptEnvironment(tempDirectory: string) {
  const environment: NodeJS.ProcessEnv = { ...process.env, TMPDIR: tempDirectory };
  delete environment['NODE_OPTIONS'];
  return environment;
}

/** How to stop each Script that is running */
const runningScripts = new Set<() => void>();

/**
 * Stops every running Script, with everything it started. For the runner to call as it stops itself: the Scripts'
 * processes lead process groups of their own, which a signal to the runner's group doesn't reach, and one whose
 * Script blocks it can't notice the runner going.
 */
export function stopAllScripts() {
  for (const stop of runningScripts) {
    stop();
  }
}

/** Kills the process group `pid` leads, if it's still there */
function killProcessGroup(pid: number) {
  try {
    process.kill(-pid, 'SIGKILL');
  } catch {
    // Everything in the group has already gone, or, on Windows, there are no process groups
  }
}

/**
 * Runs a Script's child process to completion, killing it when `signal` aborts or, after a grace period for a
 * screenshot, at the timeout. The process reports its result over IPC, so the Script's console output can't be
 * mistaken for it, and exits after reporting even if the Script left a handle open. Whichever way it ends,
 * anything it started goes with it. It runs with `execArgv`, its limits, and its temp files go in `tempDirectory`,
 * since a killed process can't clean up after itself.
 */
const runScriptProcess = (
  job: ScriptJob,
  execArgv: string[],
  timeoutMs: number,
  tempDirectory: string,
  signal: AbortSignal,
) =>
  new Promise<ScriptResult & DebugOutput>((resolve) => {
    const child = fork(scriptProcessPath, {
      execArgv,
      env: scriptEnvironment(tempDirectory),
      stdio: ['ignore', 'pipe', 'pipe', 'ipc'],
      // In a process group of its own, which everything it starts joins, so they can all be killed together
      detached: true,
    });
    let browserPid: number | undefined;
    /**
     * Kills the Script's process and everything it started. Its subprocesses join the group it leads, unless one
     * leaves on purpose, and Chromium leads a group of its own.
     */
    const stop = () => {
      for (const pid of [child.pid, browserPid]) {
        if (pid !== undefined) {
          killProcessGroup(pid);
        }
      }
      // Windows has no process groups, so there this at least kills the Script's own process
      child.kill('SIGKILL');
    };
    runningScripts.add(stop);
    signal.addEventListener('abort', stop);
    // A caller that went away before the process started won't abort again
    if (signal.aborted) {
      stop();
    }
    const { consoleOutput, stderr } = collectConsoleOutput(child);
    let outcome: ScriptResult | undefined;
    let screenshot: Screenshot = {};
    let failure: Error | undefined;
    let timedOut = false;
    let killTimer: NodeJS.Timeout | undefined;
    let drainTimer: NodeJS.Timeout | undefined;
    const timer = setTimeout(() => {
      // A Script with an outcome is only finishing up, and that outcome stands
      if (!outcome) {
        timedOut = true;
        if (child.connected) {
          child.send(timedOutMessage);
        }
      }
      killTimer = setTimeout(stop, timeoutGraceMs);
    }, timeoutMs);

    child.on('message', (message) => {
      if (!isReportFor(job.token, message)) {
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
    });
    // Failing to spawn or to send the job: 'close' still follows, once the process is gone
    child.on('error', (error) => {
      failure ??= error;
      stop();
    });
    /** Why a process that ended without reporting in time did so */
    const unreportedResult = (code: number | null, exitSignal: NodeJS.Signals | null) => {
      if (timedOut) {
        return erroredResult(`Script timed out after ${timeoutMs} ms`);
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
      // Anything the Script left running, now that its Run is over
      stop();
      // Nothing is left to stop, and the process group's ID may soon be another's
      signal.removeEventListener('abort', stop);
      runningScripts.delete(stop);
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
      const result = outcome ?? unreportedResult(code, exitSignal);
      resolve({ ...result, ...(result.outcome !== 'succeeded' && screenshot), consoleOutput: consoleOutput() });
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
      browser: config.browser,
      ...(request.proxy && { proxy: request.proxy }),
    };
    const execArgv = [
      `--max-old-space-size=${memoryLimitMb}`,
      ...permissionArgs(config, directory, tempDirectory),
    ];
    const result = await runScriptProcess(job, execArgv, request.timeoutMs, tempDirectory, signal);
    return { ...result, durationMs: Math.round(performance.now() - started) };
  } finally {
    // A killed Script's browser may still be writing its profile as it exits, so retry while it's busy.
    // Failing even so must not replace the Script's result.
    await rm(directory, { recursive: true, force: true, maxRetries: 5 }).catch((error: unknown) => {
      console.error(`Removing ${directory} failed`, error);
    });
  }
}
