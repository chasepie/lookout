import { type Request, expect, test } from '@playwright/test';
import { randomUUID } from 'node:crypto';
import { createWatch, runWatch } from './watches';

// The test host's VPN is connected through New York, exiting at 203.0.113.7 until it rotates to another 203.0.113.x.
const exitIp = /203\.0\.113\.\d+/;

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
  await expect(runs.nth(0)).toContainText(/Through the VPN: New York, 203\.0\.113\.\d+/);

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
  await page.getByRole('link', { name: 'Settings', exact: true }).click();

  const vpn = page.getByRole('region', { name: 'VPN' });
  await expect(vpn).toContainText('Connected');
  await expect(vpn).toContainText('New York');
  await expect(vpn).toContainText(exitIp);
});

// Each Run is started through the API, as the scheduler would, so only pushed events update the page.
test('see the VPN rotate to a new exit IP after a Known failure, without reloading', async ({ page }) => {
  const id = await createWatch(page, `Blocked price ${randomUUID()}`, 'https://example.com/known-failure', true);
  const statusReads: Request[] = [];
  page.on('request', (request) => { if (new URL(request.url()).pathname === '/api/vpn') {statusReads.push(request);} });
  await page.goto('/settings');
  // The page reads the status on loading, and again once its live events connect, as it may have missed a change.
  // The second read can land after the first is on screen, so wait for both before the Run.
  await expect.poll(() => statusReads.length).toBe(2);
  await (await statusReads[1]!.response())!.finished();
  const vpn = page.getByRole('region', { name: 'VPN' });
  await expect(vpn).toContainText('Connected');
  const exitBefore = (await vpn.getByText(exitIp).textContent())!;

  await runWatch(page, id, 1);

  await expect(vpn.getByText(exitIp)).not.toHaveText(exitBefore);
  await expect(vpn).toContainText('Connected');
  // The pushed status is shown as it is, without reading gluetun again
  expect(statusReads).toHaveLength(2);
});
