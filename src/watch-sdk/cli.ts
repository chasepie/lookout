#!/usr/bin/env node
import { Command, Option } from 'commander';
import { writeFile } from 'node:fs/promises';
import { extname, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { runWatch, WatchScriptCrash, type LookoutWatchScript, type RunResult } from './index.js';

interface RunCommandOptions {
  output?: string;
  headless?: boolean;
  browserless?: string;
  proxy?: string;
  screenshotOnSuccess?: boolean;
  screenshotOnKnownFailure?: boolean;
  screenshotOnCrash?: boolean;
  screenshot?: string;
}

/** What the CLI writes when the script crashed, in place of a result. */
interface CrashOutput {
  crash: { error: string };
  screenshot?: Uint8Array | undefined;
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

  let result: RunResult;
  try {
    result = await runWatch(watchScript, {
      signal: controller.signal,
      launchOptions: {
        headless: options.headless ?? false,
      },
      ...(options.browserless ? { browserlessUrl: options.browserless } : {}),
      ...(options.proxy ? { proxy: options.proxy } : {}),
      screenshotOn: {
        success: options.screenshotOnSuccess ?? false,
        knownFailure: options.screenshotOnKnownFailure ?? false,
        crash: options.screenshotOnCrash ?? false,
      },
    });
  } catch (error) {
    if (!(error instanceof WatchScriptCrash)) {
      throw error;
    }

    // Written like a result, so the screenshot gets out too; the exit code still says the script crashed
    await writeOutput({ crash: { error: error.error }, screenshot: error.screenshot }, options);
    process.exitCode = 1;
    return;
  }

  await writeOutput(result, options);
}

/**
 * Writes a result or crash as JSON to `--output`, for the Runner, with the screenshot as base64. Without `--output`
 * it's printed for a person, with the screenshot as its type and size; `--screenshot` saves the image itself.
 */
async function writeOutput(output: RunResult | CrashOutput, options: RunCommandOptions): Promise<void> {
  const { screenshot, ...rest } = output;
  if (options.output) {
    // JSON has no bytes, so the screenshot goes as base64
    const json = JSON.stringify(
      screenshot === undefined ? rest : { ...rest, screenshot: Buffer.from(screenshot).toString('base64') },
      null,
      2);
    await writeFile(resolve(process.cwd(), options.output), json);
  } else {
    console.log(JSON.stringify(
      screenshot === undefined ? rest : { ...rest, screenshot: describeImage(screenshot) },
      null,
      2));
  }

  // After the result, so a path that can't be written doesn't lose it
  if (screenshot !== undefined && options.screenshot) {
    try {
      await writeFile(resolve(process.cwd(), options.screenshot), screenshot);
    } catch (error) {
      console.warn(`Couldn't save the screenshot: ${error instanceof Error ? error.message : String(error)}`);
    }
  }
}

/** An image's type and size, such as `<PNG, 12 KiB>`. */
function describeImage(bytes: Uint8Array): string {
  const type = bytes[0] === 0x89 && bytes[1] === 0x50 && bytes[2] === 0x4e && bytes[3] === 0x47 ? 'PNG'
    : bytes[0] === 0xff && bytes[1] === 0xd8 && bytes[2] === 0xff ? 'JPEG'
    : 'image of unknown type';
  return `<${type}, ${Math.ceil(bytes.length / 1024)} KiB>`;
}

const program = new Command()
  .name('lookout-watch')
  .description('Run Lookout watch scripts');

program
  .command('run')
  .description('Run a watch script and print its result')
  .argument('<script>', 'path to a .ts or .js file whose default export implements LookoutWatchScript')
  .option('-o, --output <file>', 'write the result to a file instead of stdout')
  .option('--screenshot <file>', 'save the run\'s screenshot, if it has one, to this file')
  .option('--headless', 'run the browser in headless mode (default: false)')
  // The Runner sets BROWSERLESS_URL in the environment it starts the CLI with
  .addOption(new Option('--browserless <url>', 'run the browser on this browserless server instead of launching Chromium locally')
    .env('BROWSERLESS_URL'))
  // The Runner passes the VPN proxy in the environment, since the URL holds its password and command lines aren't private
  .addOption(new Option('--proxy <url>', 'send all browser traffic through this proxy, such as http://user:password@host:8888')
    .env('LOOKOUT_WATCH_PROXY_URL'))
  .option('--screenshot-on-success', 'take a full-page screenshot when the script succeeds without returning one')
  .option('--screenshot-on-known-failure', 'take a full-page screenshot when the script returns a known failure without one')
  .option('--screenshot-on-crash', 'take a full-page screenshot when the script throws')
  .action(async (scriptPath: string, options: RunCommandOptions) => {
    await runCommandAction(scriptPath, options);
  });

try {
  await program.parseAsync();
} catch (error) {
  console.error(error instanceof Error ? error.message : error);
  process.exitCode = 1;
}
