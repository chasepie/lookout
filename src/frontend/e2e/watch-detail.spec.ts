import { expect, test } from '@playwright/test';
import { randomUUID } from 'node:crypto';
import { createWatch } from './watches';

test('open a Watch, edit its settings, and see its Run history with a highlighted Change', async ({ page }) => {
  const name = `Changing price ${randomUUID()}`;
  await createWatch(page, name, 'https://example.com/changing');
  await page.goto('/');
  await page.getByRole('article', { name }).getByRole('link', { name }).click();

  await expect(page.getByRole('heading', { level: 1 })).toHaveText(name);
  await expect(page.getByText('Pending', { exact: true })).toBeVisible();
  const renamed = `Renamed ${randomUUID()}`;
  await page.getByLabel('Name', { exact: true }).fill(renamed);
  await page.getByLabel('Timeout (seconds)').fill('45');
  await page.getByRole('button', { name: 'Save settings' }).click();
  await expect(page.getByRole('status')).toHaveText('Settings saved.');
  await expect(page.getByRole('heading', { level: 1 })).toHaveText(renamed);

  await page.getByRole('button', { name: 'Run now' }).click();
  await expect(page.getByRole('status')).toHaveText('Run finished: Succeeded.');
  await page.getByRole('button', { name: 'Run now' }).click();
  await expect(page.getByRole('status')).toHaveText('Run finished: Succeeded. Change recorded.');

  const runs = page.getByRole('list', { name: 'Run history' }).getByRole('listitem');
  await expect(runs).toHaveCount(2);
  await expect(runs.nth(0)).toContainText('Change');
  await expect(runs.nth(1)).not.toContainText('Change');
  await expect(page.getByText('Healthy', { exact: true })).toBeVisible();

  await page.reload();
  await expect(page.getByLabel('Name', { exact: true })).toHaveValue(renamed);
  await expect(page.getByLabel('Timeout (seconds)')).toHaveValue('45');
  await expect(runs).toHaveCount(2);
});

test('pause and resume a Watch', async ({ page }) => {
  const name = `Pausable ${randomUUID()}`;
  const id = await createWatch(page, name);
  await page.goto(`/watches/${id}`);

  await page.getByRole('button', { name: 'Pause' }).click();
  await expect(page.getByText('Paused', { exact: true })).toBeVisible();
  await page.getByRole('link', { name: 'All Watches' }).click();
  await expect(page.getByRole('article', { name }).getByText('Paused', { exact: true })).toBeVisible();

  await page.getByRole('article', { name }).getByRole('link', { name }).click();
  await page.getByRole('button', { name: 'Resume' }).click();
  await expect(page.getByText('Pending', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Pause' })).toBeVisible();
});

test('delete a Watch', async ({ page }) => {
  const name = `Deletable ${randomUUID()}`;
  const id = await createWatch(page, name);
  await page.goto(`/watches/${id}`);

  await page.getByRole('button', { name: 'Delete Watch' }).click();
  await page.getByRole('button', { name: 'Delete permanently' }).click();

  await expect(page).toHaveURL('/');
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Watches');
  await expect(page.getByRole('article', { name })).toHaveCount(0);
  await page.goto(`/watches/${id}`);
  await expect(page.getByRole('alert')).toHaveText('This Watch no longer exists.');
});
