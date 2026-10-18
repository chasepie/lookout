import { execFile } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';
import { expect, test } from 'vitest';

const execFileAsync = promisify(execFile);

const runnerPath = fileURLToPath(new URL('./index.ts', import.meta.url));
const fixturePath = (name: string) =>
  fileURLToPath(new URL(`./test/fixtures/${name}`, import.meta.url));

test('runs a script and reports its outcome', { timeout: 30_000 }, async () => {
  const { stdout } = await execFileAsync(process.execPath, [
    runnerPath,
    '--scriptPath', fixturePath('report-notification-url.js'),
    '--notificationUrl', 'https://example.com/product',
    '--headless',
  ]);

  expect(JSON.parse(stdout)).toEqual({
    success: true,
    content: 'https://example.com/product',
  });
});
