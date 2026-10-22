import { expect, test } from '@playwright/test';
import { randomUUID } from 'node:crypto';
import { createWatch, runWatch } from './watches';

test('open an Errored Run and see its screenshot and console output', async ({ page }) => {
  const id = await createWatch(page, `Errored price ${randomUUID()}`, 'https://example.com/errored');
  await runWatch(page, id, 1);
  await page.goto(`/watches/${id}`);
  let debugOutputReads = 0;
  page.on('request', (request) => {
    if (request.url().endsWith('/debug-output')) {debugOutputReads++;}
  });

  const run = page.getByRole('list', { name: 'Run history' }).getByRole('listitem').first();
  await expect(run).toContainText('Page unavailable');
  await run.getByRole('button', { name: 'Show debug output' }).click();

  const screenshot = run.getByRole('img', { name: 'Screenshot of the Script\'s page when the Run ended' });
  await expect(screenshot).toBeVisible();
  await expect.poll(() => screenshot.evaluate((image: HTMLImageElement) => image.naturalWidth)).toBe(8);
  await expect(run.locator('pre.console')).toHaveText('Opening the widget page\nTimed out waiting for .price\n');

  await run.getByRole('button', { name: 'Hide debug output' }).click();
  await expect(screenshot).toHaveCount(0);

  // A finished Run's debug output doesn't change, so reopening it shows what was already read.
  await run.getByRole('button', { name: 'Show debug output' }).click();
  await expect(screenshot).toBeVisible();
  expect(debugOutputReads).toBe(1);
});

test('a Known failure Run without debug output explains why', async ({ page }) => {
  const id = await createWatch(page, `Blocked price ${randomUUID()}`, 'https://example.com/known-failure');
  await runWatch(page, id, 1);
  await page.goto(`/watches/${id}`);

  const run = page.getByRole('list', { name: 'Run history' }).getByRole('listitem').first();
  await run.getByRole('button', { name: 'Show debug output' }).click();

  await expect(run).toContainText('No debug output is kept for this Run.');
});

test('a Succeeded Run offers no debug output', async ({ page }) => {
  const id = await createWatch(page, `Healthy price ${randomUUID()}`);
  await runWatch(page, id, 1);
  await page.goto(`/watches/${id}`);

  const run = page.getByRole('list', { name: 'Run history' }).getByRole('listitem').first();
  await expect(run).toContainText('£19.99');
  await expect(run.getByRole('button', { name: 'Show debug output' })).toHaveCount(0);
});
