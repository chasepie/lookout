import type { ScriptContext } from '@chasepie/lookout-scripting';
import { fork } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { erroredResult, isReportFor, type ProxySettings, type ScriptJob, type ScriptResult } from './protocol.ts';

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

export type ExecuteResponse = ScriptResult & { durationMs: number };

const scriptProcessPath = fileURLToPath(new URL('./script-process.ts', import.meta.url));

/**
 * Runs a Script's child process to completion, killing it at the timeout or when `signal` aborts. The process
 * reports its result over IPC, so the Script's console output can't be mistaken for it, and exits after reporting
 * even if the Script left a handle open. Its temp files go in `tempDirectory`, since a killed process can't clean
 * up after itself.
 */
const runScriptProcess = (job: ScriptJob, timeoutMs: number, tempDirectory: string, signal: AbortSignal) =>
  new Promise<ScriptResult>((resolve) => {
    const child = fork(scriptProcessPath, {
      execArgv: [],
      env: { ...process.env, TMPDIR: tempDirectory },
      stdio: ['ignore', 'inherit', 'inherit', 'ipc'],
      signal,
      killSignal: 'SIGKILL',
    });
    let reported: ScriptResult | undefined;
    let failure: Error | undefined;
    let timedOut = false;
    const timer = setTimeout(() => {
      timedOut = true;
      child.kill('SIGKILL');
    }, timeoutMs);

    child.on('message', (message) => {
      if (!reported && !timedOut && isReportFor(job.token, message)) {
        reported = message.result;
      }
    });
    // Failing to spawn, to send the job, or being aborted: 'close' still follows, once the process is gone
    child.on('error', (error) => {
      failure ??= error;
      child.kill('SIGKILL');
    });
    child.once('close', (code, exitSignal) => {
      clearTimeout(timer);
      if (reported) {
        resolve(reported);
      } else if (timedOut) {
        resolve(erroredResult(`Script timed out after ${timeoutMs} ms`));
      } else if (failure) {
        resolve(erroredResult(`Script process failed: ${failure.message}`));
      } else {
        const ending = exitSignal ? `was killed by ${exitSignal}` : `exited with code ${code}`;
        resolve(erroredResult(`Script process ${ending} before reporting an outcome`));
      }
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
  const directory = await mkdtemp(join(tmpdir(), 'lookout-script-'));
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
