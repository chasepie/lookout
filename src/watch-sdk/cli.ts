#!/usr/bin/env node
import { Command } from 'commander';
import { extname, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { isRunResult, runWatch, type LookoutWatchScript } from './index.js';

function isWatchScript(value: unknown): value is LookoutWatchScript {
  return typeof value === 'object'
    && value !== null
    && 'run' in value && typeof value.run === 'function';
}

async function loadWatchScript(scriptPath: string): Promise<LookoutWatchScript> {
  const absolutePath = resolve(process.cwd(), scriptPath);

  if (['.ts', '.mts', '.cts'].includes(extname(absolutePath)) && !process.features.typescript) {
    throw new Error(`Loading ${scriptPath} requires a Node.js version that supports TypeScript (22.18+)`);
  }

  // A file URL keeps Windows paths like C:\... from being parsed as a URL scheme.
  const mod = await import(pathToFileURL(absolutePath).href) as { default?: unknown };
  const exported = mod.default;
  if (typeof exported === 'function') {
    return new (exported as new () => LookoutWatchScript)();
  }

  if (isWatchScript(exported)) {
    return exported;
  }

  throw new Error(`${scriptPath} must default-export a class or object implementing LookoutWatchScript`);
}

async function runWatchScript(scriptPath: string): Promise<void> {
  const watchScript = await loadWatchScript(scriptPath);

  // First Ctrl+C aborts the script so the browser closes cleanly; a second one exits immediately.
  const controller = new AbortController();
  process.once('SIGINT', () => controller.abort());

  const result = await runWatch(watchScript, { signal: controller.signal });
  if (!isRunResult(result)) {
    throw new Error(`Watch script returned an unexpected result: ${JSON.stringify(result)}`);
  }

  console.log(JSON.stringify(result, null, 2));
}

const program = new Command()
  .name('lookout-watch')
  .description('Run Lookout watch scripts');

program
  .command('run')
  .description('Run a watch script and print its result')
  .argument('<script>', 'path to a .ts or .js file whose default export implements LookoutWatchScript')
  .action(runWatchScript);

try {
  await program.parseAsync();
} catch (error) {
  console.error(error instanceof Error ? error.message : error);
  process.exitCode = 1;
}
