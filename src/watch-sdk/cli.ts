#!/usr/bin/env node
import { Command, Option } from 'commander';
import { writeFile } from 'node:fs/promises';
import { extname, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { isRunResult, runWatch, type LookoutWatchScript } from './index.js';

interface RunCommandOptions {
  output?: string;
  headless?: boolean;
  browserless?: string;
}

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

async function runCommandAction(scriptPath: string, options: RunCommandOptions): Promise<void> {
  const watchScript = await loadWatchScript(scriptPath);

  // The first SIGINT (Ctrl+C) or SIGTERM aborts the script so the browser closes cleanly; a second one exits immediately.
  const controller = new AbortController();
  process.once('SIGINT', () => controller.abort());
  process.once('SIGTERM', () => controller.abort());

  const result = await runWatch(watchScript, {
    signal: controller.signal,
    launchOptions: {
      headless: options.headless ?? false,
    },
    ...(options.browserless ? { browserlessUrl: options.browserless } : {}),
  });
  if (!isRunResult(result)) {
    throw new Error(`Watch script returned an unexpected result: ${JSON.stringify(result)}`);
  }

  const json = JSON.stringify(result, null, 2);
  if (options.output) {
    await writeFile(resolve(process.cwd(), options.output), json);
  } else {
    console.log(json);
  }
}

const program = new Command()
  .name('lookout-watch')
  .description('Run Lookout watch scripts');

program
  .command('run')
  .description('Run a watch script and print its result')
  .argument('<script>', 'path to a .ts or .js file whose default export implements LookoutWatchScript')
  .option('-o, --output <file>', 'write the result to a file instead of stdout')
  .option('--headless', 'run the browser in headless mode (default: false)')
  // The Runner passes its environment on to the CLI, so the AppHost's LOOKOUT_BROWSERLESS_URL reaches it
  .addOption(new Option('--browserless <url>', 'run the browser on this browserless server instead of launching Chromium locally')
    .env('LOOKOUT_BROWSERLESS_URL'))
  .action(async (scriptPath: string, options: RunCommandOptions) => {
    await runCommandAction(scriptPath, options);
  });

try {
  await program.parseAsync();
} catch (error) {
  console.error(error instanceof Error ? error.message : error);
  process.exitCode = 1;
}
