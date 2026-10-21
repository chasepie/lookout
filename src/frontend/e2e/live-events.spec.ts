import { expect, test } from '@playwright/test';
import { randomUUID } from 'node:crypto';
import { createWatch, pauseWatch, runWatch } from './watches';

// Each Run is started through the API, as another tab or the scheduler would, so only pushed events update the page.
test('see a Watch Running, then its Change, in the list and in history without reloading', async ({ page }) => {
  const name = `Live price ${randomUUID()}`;
  const id = await createWatch(page, name, 'https://example.com/slow-changing');
  await runWatch(page, id, 1);
  await page.goto('/');
  const card = page.getByRole('article', { name });
  await expect(card.getByText('Healthy', { exact: true })).toBeVisible();
  const content = await card.locator('.content').textContent();

  const listedRun = runWatch(page, id, 1);
  await expect(card.getByText('Running (Healthy)', { exact: true })).toBeVisible();
  await listedRun;
  await expect(card.getByText('Change recorded')).toBeVisible();
  await expect(card.locator('.content')).not.toHaveText(content!);
  await expect(card.getByText('Healthy', { exact: true })).toBeVisible();

  await card.getByRole('link', { name }).click();
  const runs = page.getByRole('list', { name: 'Run history' }).getByRole('listitem');
  await expect(runs).toHaveCount(2);
  const detailRun = runWatch(page, id, 1);
  await expect(page.getByText('Running (Healthy)', { exact: true })).toBeVisible();
  await detailRun;
  await expect(runs).toHaveCount(3);
  await expect(runs.nth(0)).toContainText('Change');
  await expect(page.getByText('Healthy', { exact: true })).toBeVisible();
});

test('see a Watch\'s status change when it is paused elsewhere', async ({ page }) => {
  const name = `Paused elsewhere ${randomUUID()}`;
  const id = await createWatch(page, name);
  await page.goto('/');
  const card = page.getByRole('article', { name });
  await expect(card.getByText('Pending', { exact: true })).toBeVisible();

  await pauseWatch(page, id);

  await expect(card.getByText('Paused', { exact: true })).toBeVisible();
});
