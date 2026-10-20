import { expect, test } from '@playwright/test';
import { randomUUID } from 'node:crypto';

test('create a Watch with a Script, Run now, and see the saved content', async ({ page }) => {
  const name = `Widget price ${randomUUID()}`;
  await page.goto('/');
  await page.getByLabel('Name', { exact: true }).fill(name);
  await page.getByLabel('Notification URL').fill('https://example.com/widget');
  await page.getByLabel('Script', { exact: true }).setInputFiles({
    name: 'price.ts',
    mimeType: 'text/plain',
    buffer: Buffer.from("export default { async run() { return { success: true, content: '£19.99' }; } };"),
  });
  await page.getByRole('button', { name: 'Create Watch' }).click();
  const watch = page.getByRole('article', { name });
  await expect(watch).toContainText('No Runs yet');
  await watch.getByRole('button', { name: 'Run now' }).click();
  await expect(watch).toContainText('£19.99');
  await expect(watch.getByText('Last Run: Succeeded', { exact: true })).toBeVisible();
  await page.reload();
  await expect(watch).toContainText('£19.99');
  await expect(watch).toContainText('In stock');
});

for (const [outcome, label] of [['known-failure', 'Known failure'], ['errored', 'Errored']] as const) {
  test(`labels the first ${outcome} Run as an outcome`, async ({ page }) => {
    const name = `First failure ${randomUUID()}`;
    const created = await page.request.post('/api/watches', { multipart: {
      name, notificationUrl: `https://example.com/${outcome}`,
      script: { name: 'price.ts', mimeType: 'text/plain', buffer: Buffer.from('export default {}') },
    } });
    expect(created.status()).toBe(201);
    await page.goto('/');
    const watch = page.getByRole('article', { name });
    await expect(watch.getByText('Pending', { exact: true })).toHaveCount(0);
    await watch.getByRole('button', { name: 'Run now' }).click();
    await expect(watch.getByText(`Last Run: ${label}`, { exact: true })).toBeVisible();
    await expect(watch).toContainText('No content yet');
  });
}

test('shows the backend validation message when a Script is empty', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('Name', { exact: true }).fill(`Invalid Script ${randomUUID()}`);
  await page.getByLabel('Notification URL').fill('https://example.com/widget');
  await page.getByLabel('Script', { exact: true }).setInputFiles({
    name: 'empty.ts', mimeType: 'text/plain', buffer: Buffer.from(' '),
  });
  await page.getByRole('button', { name: 'Create Watch' }).click();
  await expect(page.getByRole('alert')).toHaveText('Script source must not be empty.');
});

test('shows why Run now was rejected with a conflict', async ({ page }) => {
  const name = `Busy Watch ${randomUUID()}`;
  const created = await page.request.post('/api/watches', { multipart: {
    name, notificationUrl: 'https://example.com/widget',
    script: { name: 'price.ts', mimeType: 'text/plain', buffer: Buffer.from('export default {}') },
  } });
  expect(created.status()).toBe(201);
  await page.goto('/');
  // The backend suite proves overlapping Runs return 409; this checks its presentation in the browser.
  await page.route('**/api/watches/*/runs', (route) => route.fulfill({
    status: 409, json: { detail: 'This Watch is already running.' },
  }));
  await page.getByRole('article', { name }).getByRole('button', { name: 'Run now' }).click();
  await expect(page.getByRole('alert')).toHaveText('This Watch is already running.');
});
