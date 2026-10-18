import type { LookoutScript } from '@chasepie/lookout-scripting';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { parseArgs } from 'node:util';
import { chromium } from 'playwright';

function parseCliArgs() {
  const { values } = parseArgs({
    options: {
      scriptPath: { type: 'string' },
      notificationUrl: { type: 'string' },
      headless: { type: 'boolean', default: false },
    },
  });

  if (!values.scriptPath) {
    throw new Error('scriptPath argument is required');
  }
  if (!values.notificationUrl) {
    throw new Error('notificationUrl argument is required');
  }
  return {
    scriptPath: values.scriptPath,
    notificationUrl: values.notificationUrl,
    headless: values.headless,
  };
}

const isClass = (value: unknown): value is new () => unknown =>
  typeof value === 'function' && /^class\b/.test(Function.prototype.toString.call(value));

const isLookoutScript = (value: unknown): value is LookoutScript =>
  typeof value === 'object' && value !== null && 'run' in value && typeof value.run === 'function';

/** Loads a script file, whose default export is a LookoutScript object or a class implementing one */
async function loadScript(scriptPath: string): Promise<LookoutScript> {
  const scriptModule: { default?: unknown } = await import(pathToFileURL(resolve(scriptPath)).href);
  const exported = scriptModule.default;
  const script = isClass(exported) ? new exported() : exported;

  if (!isLookoutScript(script)) {
    throw new Error('Script must default-export a LookoutScript object or a class implementing it');
  }
  return script;
}

async function main() {
  const cliArgs = parseCliArgs();
  const script = await loadScript(cliArgs.scriptPath);

  const browser = await chromium.launch({
    headless: cliArgs.headless,
    args: ['--disable-blink-features=AutomationControlled'],
  });
  try {
    const page = await browser.newPage();
    const outcome = await script.run(page, { notificationUrl: cliArgs.notificationUrl });
    console.log(JSON.stringify(outcome));
  } finally {
    await browser.close();
  }
}

await main();
