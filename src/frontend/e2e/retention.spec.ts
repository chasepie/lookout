import { expect, test } from '@playwright/test';

test('change how long Runs are kept from Settings', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('link', { name: 'Settings', exact: true }).click();

  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Settings');
  const retention = page.getByRole('region', { name: 'Run history' });
  const days = retention.getByLabel('Keep Runs for (days)');
  await expect(days).not.toHaveValue('');
  // The setting is install-wide, so it is put back for other tests and later runs against the same backend.
  const original = await days.inputValue();
  const changed = original === '14' ? '15' : '14';

  await days.fill('0');
  await days.blur();
  await expect(retention).toContainText('Enter a whole number of days between 1 and 3650.');
  await expect(retention.getByRole('button', { name: 'Save' })).toBeDisabled();

  await days.fill(changed);
  await retention.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByRole('status')).toHaveText('Run history settings saved.');
  await expect(days).toHaveValue(changed);

  await page.reload();
  await expect(days).toHaveValue(changed);

  await days.fill(original);
  await retention.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByRole('status')).toHaveText('Run history settings saved.');
});
