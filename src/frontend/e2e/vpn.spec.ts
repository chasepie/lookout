import { expect, test } from '@playwright/test';
import { randomUUID } from 'node:crypto';

// The test host's VPN is connected through New York, exiting at 203.0.113.7.

test('create a VPN Watch, see its Runs record the VPN exit, then send its Runs direct', async ({ page }) => {
  const name = `VPN price ${randomUUID()}`;
  await page.goto('/');
  await page.getByLabel('Name', { exact: true }).fill(name);
  await page.getByLabel('Notification URL').fill('https://example.com/widget');
  await page.getByLabel('Script', { exact: true }).setInputFiles({
    name: 'price.ts', mimeType: 'text/plain', buffer: Buffer.from('export default {}'),
  });
  await page.getByLabel('Send Runs through the VPN').check();
  await page.getByRole('button', { name: 'Create Watch' }).click();
  const watch = page.getByRole('article', { name });
  await expect(watch.getByText('VPN', { exact: true })).toBeVisible();
  await watch.getByRole('link', { name }).click();

  await expect(page.getByLabel('Send Runs through the VPN')).toBeChecked();
  await page.getByRole('button', { name: 'Run now' }).click();
  await expect(page.getByRole('status')).toHaveText('Run finished: Succeeded.');
  const runs = page.getByRole('list', { name: 'Run history' }).getByRole('listitem');
  await expect(runs.nth(0)).toContainText('Through the VPN: New York, 203.0.113.7');

  await page.getByLabel('Send Runs through the VPN').uncheck();
  await page.getByRole('button', { name: 'Save settings' }).click();
  await expect(page.getByRole('status')).toHaveText('Settings saved.');
  await page.getByRole('button', { name: 'Run now' }).click();
  await expect(runs).toHaveCount(2);
  await expect(runs.nth(0)).not.toContainText('Through the VPN');
  await page.reload();
  await expect(page.getByLabel('Send Runs through the VPN')).not.toBeChecked();
});

test('see the VPN status in Settings', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('link', { name: 'Settings' }).click();

  const vpn = page.getByRole('region', { name: 'VPN' });
  await expect(vpn).toContainText('Connected');
  await expect(vpn).toContainText('New York');
  await expect(vpn).toContainText('203.0.113.7');
});
