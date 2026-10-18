import type { LookoutScript, LookoutScriptExport } from '@chasepie/lookout-scripting';
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

/** Loads a script file, whose default export is a LookoutScript object or a class implementing one */
async function loadScript(scriptPath: string): Promise<LookoutScript> {
  const scriptModule: { default?: LookoutScriptExport } = await import(pathToFileURL(resolve(scriptPath)).href);
  const script = scriptModule.default;

  if (typeof script === 'function') {
    return new script();
  }
  if (typeof script?.run === 'function') {
    return script;
  }
  throw new Error('Script must default-export a LookoutScript object or a class implementing it');
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
