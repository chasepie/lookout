import { expect, test } from '@playwright/test';
import { randomUUID } from 'node:crypto';

test('sends the user to log in when their session ends, then back to their Watches', async ({ page }) => {
  const name = `Session check ${randomUUID()}`;
  const created = await page.request.post('/api/watches', { multipart: {
    name, notificationUrl: 'https://example.com/widget',
    script: { name: 'price.ts', mimeType: 'text/plain', buffer: Buffer.from('export default {}') },
  } });
  expect(created.status()).toBe(201);
  await page.goto('/');
  await expect(page.getByRole('article', { name })).toBeVisible();

  // Without its cookie the backend no longer recognises the session, as after a logout elsewhere.
  await page.context().clearCookies();
  await page.getByRole('article', { name }).getByRole('button', { name: 'Run now' }).click();

  await expect(page.getByRole('heading', { name: 'Log in' })).toBeVisible();
  await page.getByLabel('User name').fill('owner');
  await page.getByLabel('Password').fill('correct horse');
  await page.getByRole('button', { name: 'Log in' }).click();
  await expect(page.getByRole('article', { name })).toBeVisible();
});
