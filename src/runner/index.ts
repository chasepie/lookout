import { parseArgs, type ParseArgsConfig } from 'node:util';
import { chromium } from 'playwright';
import { } from 'node:fs/promises';

function parseCliArgs() {
  const cliArgConfig: ParseArgsConfig = {
    options: {
      scriptPath: { type: 'string' },
      headless: { default: false, type: 'boolean' }
    }
  };
  const { values } = parseArgs(cliArgConfig);

  if (typeof values['headless'] !== 'boolean') {
    throw new Error('Invalid value for headless argument');
  }
  const scriptPath = values['scriptPath'];
  if (!scriptPath) {
    throw new Error('scriptPath argument is required');
  }
  if ()
    return { headless: values['headless'], scriptPath: values['scriptPath'] };
}


async function main() {
  const cliArgs = parseCliArgs();

  const browser = await chromium.launch({
    headless: cliArgs.headless,
    args: ['--disable-blink-features=AutomationControlled'],
  });
}

main();
