import { expect, test } from '@playwright/test';
import { ownerStorageState } from './owner';

// Runs before every other test, against a backend with no account, and saves the login they reuse.
test('first launch: set up the account, log out, and log in again', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Set up Lookout' })).toBeVisible();
  await page.getByLabel('User name').fill('owner');
  await page.getByLabel('Password', { exact: true }).fill('correct horse');
  await page.getByLabel('Confirm password').fill('correct horse');
  await page.getByRole('button', { name: 'Create account' }).click();
  await expect(page.getByRole('heading', { name: 'Watches' })).toBeVisible();
  await expect(page.getByText('Logged in as owner')).toBeVisible();

  await page.getByRole('button', { name: 'Log out' }).click();
  await expect(page.getByRole('heading', { name: 'Log in' })).toBeVisible();
  await page.reload();
  // Setup is only offered while no account exists.
  await expect(page.getByRole('heading', { name: 'Log in' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Set up Lookout' })).toHaveCount(0);
  await page.getByLabel('User name').fill('owner');
  await page.getByLabel('Password').fill('wrong password');
  await page.getByRole('button', { name: 'Log in' }).click();
  await expect(page.getByRole('alert')).toHaveText('Incorrect user name or password.');
  await page.getByLabel('Password').fill('correct horse');
  await page.getByRole('button', { name: 'Log in' }).click();
  await expect(page.getByRole('heading', { name: 'Watches' })).toBeVisible();
  await page.reload();
  await expect(page.getByText('Logged in as owner')).toBeVisible();

  await page.context().storageState({ path: ownerStorageState });
});
