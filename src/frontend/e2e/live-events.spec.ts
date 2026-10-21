import { expect, test } from '@playwright/test';
import { randomUUID } from 'node:crypto';
import { holdFirst } from './hold';
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

test('older Runs on screen stay when another Run finishes', async ({ page }) => {
  const id = await createWatch(page, `Long history ${randomUUID()}`);
  await runWatch(page, id, 51);
  await page.goto(`/watches/${id}`);
  const runs = page.getByRole('list', { name: 'Run history' }).getByRole('listitem');
  await page.getByRole('button', { name: 'Show older Runs' }).click();
  await expect(runs).toHaveCount(51);

  await runWatch(page, id, 1);

  await expect(runs).toHaveCount(52);
});

test('the page stays when live events drop while the backend is unreachable', async ({ page }) => {
  const name = `Restarting backend ${randomUUID()}`;
  await createWatch(page, name);
  // Every connection, whichever transport it falls back to, fails at once, as when the backend restarts.
  await page.routeWebSocket(/\/api\/events/, (socket) => socket.close());
  await page.route(/\/api\/events\?id=/, (route) => route.abort('connectionrefused'));
  await page.goto('/');
  await expect(page.getByRole('article', { name })).toBeVisible();

  await page.route('**/api/session', (route) => route.abort('connectionrefused'));
  await page.waitForEvent('requestfailed', (request) => request.url().endsWith('/api/session'));
  // Nothing signals a page that must not be replaced, so give it time to be.
  await page.waitForTimeout(300);

  await expect(page.getByRole('heading', { name: 'Could not reach Lookout' })).toHaveCount(0);
  await expect(page.getByRole('article', { name })).toBeVisible();
});

test('connecting while the Watches list loads reads it again without cancelling the read under way', async ({ page }) => {
  const name = `Connects mid-read ${randomUUID()}`;
  await createWatch(page, name);
  const aborted: string[] = [];
  page.on('requestfailed', (request) => aborted.push(`${request.method()} ${request.url()}`));
  const release = await holdFirst(page, '**/api/watches', 'GET');
  const socket = page.waitForEvent('websocket');
  await page.goto('/');
  // The connection's handshake answered, so it has opened while the list's first read is still held.
  await (await socket).waitForEvent('framereceived');
  let listReads = 0;
  page.on('response', (response) => {
    if (response.request().method() === 'GET' && new URL(response.url()).pathname === '/api/watches') {listReads++;}
  });

  release();

  // The held read lands, then the list is read again, as the held read may predate what the connection missed.
  await expect.poll(() => listReads).toBeGreaterThanOrEqual(2);
  await expect(page.getByRole('article', { name })).toBeVisible();
  expect(aborted).toEqual([]);
});
