import { execFile } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';
import { expect, test } from 'vitest';

const execFileAsync = promisify(execFile);

const runnerPath = fileURLToPath(new URL('./index.ts', import.meta.url));
const fixturePath = (name: string) =>
  fileURLToPath(new URL(`./test/fixtures/${name}`, import.meta.url));

const runScript = (fixture: string) =>
  execFileAsync(process.execPath, [
    runnerPath,
    '--scriptPath', fixturePath(fixture),
    '--notificationUrl', 'https://example.com/product?a=1&copy=2',
    '--headless',
  ]);

test('runs a script and reports its outcome', { timeout: 30_000 }, async () => {
  const { stdout } = await runScript('report-notification-url.ts');

  expect(JSON.parse(stdout)).toEqual({
    success: true,
    content: 'https://example.com/product?a=1&copy=2',
  });
});

test('runs a script that default-exports a class', { timeout: 30_000 }, async () => {
  const { stdout } = await runScript('class-export.ts');

  expect(JSON.parse(stdout)).toEqual({
    success: true,
    content: 'class ran for https://example.com/product?a=1&copy=2',
  });
});

test.each([
  ['a plain function', 'function-export.ts'],
  ['a class without run', 'class-without-run.ts'],
  ['no default export', 'named-export.ts'],
])('rejects a script with %s', { timeout: 30_000 }, async (_, fixture) => {
  const result = runScript(fixture);

  await expect(result).rejects.toMatchObject({
    stdout: '',
    stderr: expect.stringContaining(
      'Script must default-export a LookoutScript object or a class implementing it'),
  });
});
