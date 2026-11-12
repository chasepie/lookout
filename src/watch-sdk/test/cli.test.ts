import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { after, before, describe, test } from 'node:test';
import { fileURLToPath } from 'node:url';

// The built CLI, as the Runner runs it: `npm run build` first
const cliPath = fileURLToPath(new URL('../dist/cli.js', import.meta.url));

let workDirectory: string;

before(async () => {
  workDirectory = await mkdtemp(join(tmpdir(), 'lookout-watch-cli-tests-'));
});

after(async () => {
  await rm(workDirectory, { recursive: true, force: true });
});

/** What the CLI writes: a result or a crash, with the screenshot as base64. */
interface CliOutput {
  success?: unknown;
  content?: unknown;
  failureReason?: unknown;
  failover?: unknown;
  crash?: { error?: unknown };
  screenshot?: unknown;
}

interface CliRun {
  exitCode: number;
  stdout: string;
  stderr: string;
  /** What the CLI wrote to `--output`, parsed, or `undefined` if it wrote nothing. */
  output: CliOutput | undefined;
}

let runCount = 0;

/**
 * Runs a watch script with the CLI in local headless Chromium, writing its result to `--output`.
 * @param script The script's source: a module whose default export implements `LookoutWatchScript`.
 */
async function runCli(script: string, args: string[] = [], { output = true } = {}): Promise<CliRun> {
  runCount++;
  const scriptPath = join(workDirectory, `watch-${runCount}.mjs`);
  const outputPath = join(workDirectory, `output-${runCount}.json`);
  await writeFile(scriptPath, script);

  // Without the Runner's variables, so the CLI launches Chromium here rather than on browserless
  const env = Object.fromEntries(Object.entries(process.env)
    .filter(([name]) => name !== 'BROWSERLESS_URL' && name !== 'LOOKOUT_WATCH_PROXY_URL'));

  const cliArgs = [cliPath, 'run', scriptPath, '--headless', ...(output ? ['--output', outputPath] : []), ...args];
  const { exitCode, stdout, stderr } = await new Promise<{ exitCode: number; stdout: string; stderr: string }>(resolve => {
    execFile(process.execPath, cliArgs, { env, cwd: workDirectory }, (error, stdout, stderr) => {
      resolve({ exitCode: error === null ? 0 : typeof error.code === 'number' ? error.code : 1, stdout, stderr });
    });
  });

  let parsed: CliOutput | undefined;
  try {
    parsed = JSON.parse(await readFile(outputPath, 'utf8')) as CliOutput;
  } catch {
    parsed = undefined;
  }

  return { exitCode, stdout, stderr, output: parsed };
}

type ImageType = 'png' | 'jpeg' | 'other';

function imageTypeOf(bytes: Uint8Array): ImageType {
  if (bytes[0] === 0x89 && bytes[1] === 0x50 && bytes[2] === 0x4e && bytes[3] === 0x47) {
    return 'png';
  }

  return bytes[0] === 0xff && bytes[1] === 0xd8 && bytes[2] === 0xff ? 'jpeg' : 'other';
}

/** The type of the screenshot in the CLI's output, decoded from base64, or `undefined` if it has none. */
function screenshotTypeOf(output: CliOutput | undefined): ImageType | undefined {
  const screenshot = output?.screenshot;
  if (screenshot === undefined) {
    return undefined;
  }

  assert.equal(typeof screenshot, 'string');
  return imageTypeOf(Buffer.from(screenshot as string, 'base64'));
}

describe('a screenshot the script returns', () => {
  test('is written with a success', async () => {
    const run = await runCli(`export default {
      async run(page) {
        await page.setContent('<h1>Hello</h1>');
        return { success: true, content: 'Hello', screenshot: await page.screenshot() };
      },
    };`);

    assert.equal(run.exitCode, 0, run.stderr);
    assert.equal(run.output?.content, 'Hello');
    assert.equal(screenshotTypeOf(run.output), 'png');
  });
});

/** A JPEG's height in pixels, from its start-of-frame segment. */
function jpegHeightOf(bytes: Uint8Array): number {
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  let offset = 2;
  while (offset < bytes.length) {
    const marker = view.getUint16(offset);
    // SOF0 to SOF15, except DHT (C4), JPG (C8) and DAC (CC)
    if (marker >= 0xffc0 && marker <= 0xffcf && ![0xffc4, 0xffc8, 0xffcc].includes(marker)) {
      return view.getUint16(offset + 5);
    }

    offset += 2 + view.getUint16(offset + 2);
  }

  throw new Error('The JPEG has no start-of-frame segment.');
}

// Taller than the viewport, so only a full-page screenshot is this tall
const tallPage = `await page.setContent('<div style="height: 3000px">Tall</div>');`;

describe('an automatic screenshot', () => {
  test('of the whole page is taken for a success with --screenshot-on-success', async () => {
    const run = await runCli(`export default {
      async run(page) {
        ${tallPage}
        return { success: true, content: 'Tall' };
      },
    };`, ['--screenshot-on-success']);

    assert.equal(run.exitCode, 0, run.stderr);
    assert.equal(run.output?.content, 'Tall');
    assert.equal(screenshotTypeOf(run.output), 'jpeg');
    assert.ok(jpegHeightOf(Buffer.from(run.output?.screenshot as string, 'base64')) >= 3000);
  });

  test('is taken for a known failure with --screenshot-on-known-failure', async () => {
    const run = await runCli(`export default {
      async run(page) {
        ${tallPage}
        return { success: false, failureReason: 'Blocked' };
      },
    };`, ['--screenshot-on-known-failure']);

    assert.equal(run.exitCode, 0, run.stderr);
    assert.equal(run.output?.failureReason, 'Blocked');
    assert.equal(screenshotTypeOf(run.output), 'jpeg');
  });

  test('is only taken for the outcome its flag names', async () => {
    const run = await runCli(`export default {
      async run(page) {
        ${tallPage}
        return { success: true, content: 'Tall' };
      },
    };`, ['--screenshot-on-known-failure', '--screenshot-on-crash']);

    assert.equal(run.exitCode, 0, run.stderr);
    assert.equal(screenshotTypeOf(run.output), undefined);
  });

  test('is not taken when the script returned its own', async () => {
    const run = await runCli(`export default {
      async run(page) {
        ${tallPage}
        return { success: true, content: 'Tall', screenshot: await page.screenshot({ type: 'png' }) };
      },
    };`, ['--screenshot-on-success']);

    assert.equal(run.exitCode, 0, run.stderr);
    assert.equal(screenshotTypeOf(run.output), 'png');
  });
});

describe('an automatic screenshot that can\'t be taken', () => {
  test('leaves a success as it was', async () => {
    const run = await runCli(`export default {
      async run(page) {
        await page.close();
        return { success: true, content: 'Closed' };
      },
    };`, ['--screenshot-on-success']);

    assert.equal(run.exitCode, 0, run.stderr);
    assert.equal(run.output?.content, 'Closed');
    assert.equal(screenshotTypeOf(run.output), undefined);
  });

  test('is dropped when it\'s over 8 MiB', async () => {
    // Random noise, which JPEG can't compress, over a page big enough to make well over 8 MiB
    const run = await runCli(`export default {
      async run(page) {
        await page.setContent('<canvas width="3000" height="6000" style="display: block"></canvas>');
        await page.evaluate(() => {
          const canvas = document.querySelector('canvas');
          const context = canvas.getContext('2d');
          const image = context.createImageData(canvas.width, canvas.height);
          for (let i = 0; i < image.data.length; i += 65536) {
            crypto.getRandomValues(image.data.subarray(i, Math.min(i + 65536, image.data.length)));
          }
          for (let i = 3; i < image.data.length; i += 4) {
            image.data[i] = 255;
          }
          context.putImageData(image, 0, 0);
        });
        return { success: true, content: 'Noise' };
      },
    };`, ['--screenshot-on-success']);

    assert.equal(run.exitCode, 0, run.stderr);
    assert.equal(run.output?.content, 'Noise');
    assert.equal(screenshotTypeOf(run.output), undefined);
    assert.match(run.stderr, /8 MiB/);
  });

  test('leaves a crash with the script\'s own error', async () => {
    const run = await runCli(`export default {
      async run(page) {
        await page.close();
        throw new Error('Gave up');
      },
    };`, ['--screenshot-on-crash']);

    assert.equal(run.exitCode, 1);
    assert.match(String(run.output?.crash?.error), /^Error: Gave up/);
    assert.equal(screenshotTypeOf(run.output), undefined);
  });
});

describe('a known failure', () => {
  test('is written with its request for a failover', async () => {
    const run = await runCli(`export default {
      async run() {
        return { success: false, failureReason: 'Blocked in this region', failover: true };
      },
    };`);

    assert.equal(run.exitCode, 0, run.stderr);
    assert.equal(run.output?.failureReason, 'Blocked in this region');
    assert.equal(run.output?.failover, true);
  });

  test('is written without a failover when it asks for none', async () => {
    const run = await runCli(`export default {
      async run() {
        return { success: false, failureReason: 'Sold out' };
      },
    };`);

    assert.equal(run.exitCode, 0, run.stderr);
    assert.equal(run.output?.failover, undefined);
  });
});

describe('a crash', () => {
  const throwingScript = `export default {
    async run(page) {
      ${tallPage}
      throw new Error('No price on the page');
    },
  };`;

  test('is written with the error\'s stack, and the CLI exits with 1', async () => {
    const run = await runCli(throwingScript);

    assert.equal(run.exitCode, 1);
    assert.match(String(run.output?.crash?.error), /^Error: No price on the page\n\s+at /);
    assert.equal(run.output?.success, undefined);
    assert.equal(screenshotTypeOf(run.output), undefined);
  });

  test('gets a full-page screenshot with --screenshot-on-crash', async () => {
    const run = await runCli(throwingScript, ['--screenshot-on-crash']);

    assert.equal(run.exitCode, 1);
    assert.equal(screenshotTypeOf(run.output), 'jpeg');
  });

  test('is what a script that returns something other than a result has', async () => {
    const run = await runCli(`export default {
      async run(page) {
        ${tallPage}
        return { content: 'No success field' };
      },
    };`, ['--screenshot-on-crash']);

    assert.equal(run.exitCode, 1);
    assert.match(String(run.output?.crash?.error), /unexpected result/);
    assert.equal(screenshotTypeOf(run.output), 'jpeg');
  });

  test('is what a script that returns a screenshot that isn\'t bytes has', async () => {
    const run = await runCli(`export default {
      async run(page) {
        ${tallPage}
        return { success: true, content: 'Tall', screenshot: null };
      },
    };`, ['--screenshot-on-crash']);

    assert.equal(run.exitCode, 1);
    assert.match(String(run.output?.crash?.error), /screenshot must be bytes/);
    assert.equal(screenshotTypeOf(run.output), 'jpeg');
  });

  test('is what a script that asks for a failover with something other than a boolean has', async () => {
    const run = await runCli(`export default {
      async run() {
        return { success: false, failureReason: 'Blocked', failover: 'yes' };
      },
    };`);

    assert.equal(run.exitCode, 1);
    assert.match(String(run.output?.crash?.error), /failover must be true or false/);
  });

  test('says what the script threw when it isn\'t an error', async () => {
    const run = await runCli(`export default {
      async run() {
        throw { code: 'NO_PRICE' };
      },
    };`);

    assert.equal(run.exitCode, 1);
    assert.match(String(run.output?.crash?.error), /code: 'NO_PRICE'/);
  });

  test('is not written when the script never ran', async () => {
    const run = await runCli(`throw new Error('Broken at load');`, ['--screenshot-on-crash']);

    assert.equal(run.exitCode, 1);
    assert.equal(run.output, undefined);
    assert.match(run.stderr, /Broken at load/);
  });
});

describe('run by hand', () => {
  const screenshotScript = `export default {
    async run(page) {
      await page.setContent('<h1>Hello</h1>');
      return { success: true, content: 'Hello', screenshot: await page.screenshot() };
    },
  };`;

  test('prints the screenshot as its type and size', async () => {
    const run = await runCli(screenshotScript, [], { output: false });

    assert.equal(run.exitCode, 0, run.stderr);
    const printed = JSON.parse(run.stdout) as CliOutput;
    assert.equal(printed.content, 'Hello');
    assert.match(String(printed.screenshot), /^<PNG, \d+ KiB>$/);
  });

  test('saves the screenshot with --screenshot', async () => {
    const screenshotPath = join(workDirectory, 'saved-screenshot.png');

    const run = await runCli(screenshotScript, ['--screenshot', screenshotPath], { output: false });

    assert.equal(run.exitCode, 0, run.stderr);
    assert.equal(imageTypeOf(await readFile(screenshotPath)), 'png');
  });

  test('still prints the result when --screenshot can\'t be saved', async () => {
    const screenshotPath = join(workDirectory, 'no-such-directory', 'screenshot.png');

    const run = await runCli(screenshotScript, ['--screenshot', screenshotPath], { output: false });

    assert.equal(run.exitCode, 0, run.stderr);
    assert.equal((JSON.parse(run.stdout) as CliOutput).content, 'Hello');
    assert.match(run.stderr, /Couldn't save the screenshot/);
  });
});
