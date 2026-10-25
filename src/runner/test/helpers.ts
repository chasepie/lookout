import { execFile, spawn } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { once } from 'node:events';
import { readFile } from 'node:fs/promises';
import { createServer, type Server } from 'node:http';
import type { AddressInfo } from 'node:net';
import { fileURLToPath } from 'node:url';
import type { ProxySettings } from '../browser.ts';
import type { ExecuteRequest, ExecuteScript, FileKind } from '../execute.ts';

export const runnerPath = fileURLToPath(new URL('../index.ts', import.meta.url));

export const fixtureSource = (name: string) =>
  readFile(new URL(`./fixtures/${name}`, import.meta.url), 'utf8');

/** Pages the fixture Scripts read, served locally so no test needs the internet */
const pages: Record<string, string> = {
  '/in-stock': '<h1>Widget</h1><p class="price">£20.00</p>',
  '/blocked': '<h1>Access Denied</h1>',
};

export const listen = async (server: Server) => {
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  return `http://127.0.0.1:${(server.address() as AddressInfo).port}`;
};

/** Starts a server for the pages the fixture Scripts read */
export async function servePages() {
  const server = createServer((request, response) => {
    const page = pages[request.url ?? ''];
    response.writeHead(page === undefined ? 404 : 200, { 'content-type': 'text/html; charset=utf-8' });
    response.end(page ?? 'Not found');
  });
  return { server, url: await listen(server) };
}

export interface FixtureOptions {
  fileKind?: FileKind;
  notificationUrl: string;
  timeoutMs?: number;
  proxy?: ProxySettings;
  alwaysScreenshot?: boolean;
}

/** A request to execute a fixture Script */
export const fixtureRequest = async (fixture: string, options: FixtureOptions): Promise<ExecuteRequest> => ({
  source: await fixtureSource(fixture),
  fileKind: options.fileKind ?? 'ts',
  context: { notificationUrl: options.notificationUrl },
  timeoutMs: options.timeoutMs ?? 20_000,
  ...(options.proxy && { proxy: options.proxy }),
  alwaysScreenshot: options.alwaysScreenshot ?? false,
});

/**
 * Executes a fixture Script with `execute`, separating what every response has (its duration and console output)
 * and its debugging screenshot from the rest of the response
 */
export async function executeFixture(execute: ExecuteScript, fixture: string, options: FixtureOptions) {
  const { durationMs, consoleOutput, screenshot, ...result } =
    await execute(await fixtureRequest(fixture, options), new AbortController().signal);
  return { durationMs, consoleOutput, screenshot, result: result as Record<string, unknown> };
}

const pngSignature = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

export const isPng = (screenshot: unknown) =>
  typeof screenshot === 'string' && Buffer.from(screenshot, 'base64').subarray(0, pngSignature.length).equals(pngSignature);

/** The direct children of `pid` still running, such as a runner's Script processes */
export const childPids = (pid: number) =>
  new Promise<number[]>((resolve) => {
    execFile('pgrep', ['-P', String(pid)], (_, stdout) => resolve(stdout.split('\n').filter(Boolean).map(Number)));
  });

export const hasChildProcesses = async (pid: number) => (await childPids(pid)).length > 0;

/** The processes with `marker` in their command line, such as a subprocess the `starts-subprocess.ts` fixture started */
const markedPids = (marker: string) =>
  new Promise<number[]>((resolve) => {
    execFile('pgrep', ['-f', marker], (_, stdout) => resolve(stdout.split('\n').filter(Boolean).map(Number)));
  });

export const hasMarkedProcesses = async (marker: string) => (await markedPids(marker)).length > 0;

/** A Notification URL for `starts-subprocess.ts`, with a fresh marker for the subprocess it starts */
export function subprocessScript(...flags: ('inherit' | 'hang')[]) {
  const marker = `lookout-test-subprocess-${randomUUID()}`;
  const url = new URL('https://example.com/');
  url.searchParams.set('marker', marker);
  for (const flag of flags) {
    url.searchParams.set(flag, '');
  }
  return { marker, notificationUrl: url.href };
}

export const isAlive = (pid: number) => {
  try {
    process.kill(pid, 0);
    return true;
  } catch {
    return false;
  }
};

/** Polls until `check` holds, failing once `timeoutMs` passes */
export async function eventually(check: () => boolean | Promise<boolean>, timeoutMs = 5_000) {
  const deadline = Date.now() + timeoutMs;
  while (!await check()) {
    if (Date.now() > deadline) {
      throw new Error(`Condition still false after ${timeoutMs} ms`);
    }
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
}

/** The processes a Script's process started, such as Chromium, once it has started any */
export async function scriptSubprocesses(runnerPid: number) {
  let found: number[] = [];
  await eventually(async () => {
    const [scriptPid] = await childPids(runnerPid);
    found = scriptPid === undefined ? [] : await childPids(scriptPid);
    return found.length > 0;
  });
  return found;
}

/**
 * The environment for a real runner process: this one's, without the runner's own settings, which a developer may have
 * set for running it, and on a free port, with `env` over the top
 */
export function runnerEnvironment(env: Record<string, string>) {
  const environment: NodeJS.ProcessEnv = { ...process.env, PORT: '0', ...env };
  for (const name of ['RUNNER_HOST', 'RUNNER_TOKEN']) {
    if (!(name in env)) {
      delete environment[name];
    }
  }
  return environment;
}

/** Starts a real runner process and resolves with its base URL once it is listening */
export async function startRunner(env: Record<string, string>) {
  const runner = spawn(process.execPath, [runnerPath], {
    env: runnerEnvironment(env),
    stdio: ['ignore', 'pipe', 'inherit'],
  });
  let output = '';
  for await (const chunk of runner.stdout) {
    output += String(chunk);
    const match = /listening on (http:\/\/\S+)/.exec(output);
    if (match?.[1]) {
      return { runner, url: match[1] };
    }
  }
  throw new Error(`Runner exited before listening: ${output}`);
}
