import { expect, test } from '@playwright/test';
import { randomUUID } from 'node:crypto';
import { createWatch } from './watches';

test('sends the user to log in when their session ends, then back to their Watches', async ({ page }) => {
  const name = `Session check ${randomUUID()}`;
  await createWatch(page, name);
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

test.describe('without a session', () => {
  test.use({ storageState: { cookies: [], origins: [] } });

  test('sends the user to log in when another browser set up the account first', async ({ page }) => {
    // This browser loaded setup before the account existed; the backend's account was created since.
    let stale = true;
    await page.route('**/api/session', async (route) => {
      if (stale && route.request().method() === 'GET') {
        stale = false;
        // Keep the real response's request token; only its account status is out of date.
        const response = await route.fetch();
        return route.fulfill({ response, json: { accountExists: false, userName: null } });
      }
      return route.fallback();
    });
    await page.goto('/');
    await expect(page.getByRole('heading', { name: 'Set up Lookout' })).toBeVisible();
    await page.getByLabel('User name').fill('someone else');
    await page.getByLabel('Password', { exact: true }).fill('another password');
    await page.getByLabel('Confirm password').fill('another password');

    await page.getByRole('button', { name: 'Create account' }).click();

    await expect(page.getByRole('heading', { name: 'Log in' })).toBeVisible();
    await page.getByLabel('User name').fill('owner');
    await page.getByLabel('Password').fill('correct horse');
    await page.getByRole('button', { name: 'Log in' }).click();
    await expect(page.getByText('Logged in as owner')).toBeVisible();
  });
});
