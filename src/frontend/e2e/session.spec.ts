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
  // Any request reveals the ended session: running the Watch, or a read the app makes on its own, such as when its
  // live events connect, which may get there first.
  const logIn = page.getByRole('heading', { name: 'Log in' });
  await expect(async () => {
    if (!await logIn.isVisible()) {
      await page.getByRole('article', { name }).getByRole('button', { name: 'Run now' }).click({ timeout: 1000 });
    }
    await expect(logIn).toBeVisible({ timeout: 1000 });
  }).toPass();

  await page.getByLabel('User name').fill('owner');
  await page.getByLabel('Password').fill('correct horse');
  await page.getByRole('button', { name: 'Log in' }).click();
  await expect(page.getByRole('article', { name })).toBeVisible();
});

test('recovers when the request token goes missing, such as when another app on the host overwrites it', async ({ page }) => {
  const name = `Lost token ${randomUUID()}`;
  await createWatch(page, name);
  await page.goto('/');
  await expect(page.getByRole('article', { name })).toBeVisible();

  await page.context().clearCookies({ name: 'Lookout.XSRF-TOKEN' });
  await page.getByRole('article', { name }).getByRole('button', { name: 'Run now' }).click();

  await expect(page.getByRole('status')).toHaveText(`Run of ${name} finished: Succeeded.`);
  await expect(page.getByRole('alert')).toHaveCount(0);
});

test('an error from logging out is cleared on moving to another page', async ({ page }) => {
  const name = `After logout error ${randomUUID()}`;
  await createWatch(page, name);
  await page.route('**/api/session', (route) => route.request().method() === 'DELETE'
    ? route.fulfill({ status: 500 })
    : route.fallback());
  await page.goto('/');

  await page.getByRole('button', { name: 'Log out' }).click();
  await expect(page.getByRole('alert')).toHaveText('Could not log out. Please try again.');
  await page.getByRole('article', { name }).getByRole('link', { name }).click();

  await expect(page.getByRole('heading', { level: 1 })).toHaveText(name);
  await expect(page.getByText('Could not log out. Please try again.')).toHaveCount(0);
});

test.describe('without a session', () => {
  test.use({ storageState: { cookies: [], origins: [] } });

  test('says how long to wait after too many failed logins', async ({ page }) => {
    // Reaching the real limit would refuse every other test's logins from this address for 5 minutes.
    await page.route('**/api/session', (route) => route.request().method() === 'POST'
      ? route.fulfill({
        status: 429,
        headers: { 'Retry-After': '240' },
        contentType: 'application/problem+json',
        json: { status: 429, detail: 'Too many failed logins. Try again in 4 minutes.' },
      })
      : route.fallback());
    await page.goto('/');
    await page.getByLabel('User name').fill('owner');
    await page.getByLabel('Password').fill('a guess');

    await page.getByRole('button', { name: 'Log in' }).click();

    await expect(page.getByRole('alert')).toHaveText('Too many failed logins. Try again in 4 minutes.');
    await expect(page.getByRole('heading', { name: 'Log in' })).toBeVisible();
  });

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
