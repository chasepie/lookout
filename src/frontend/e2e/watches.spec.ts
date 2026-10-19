import { expect, test } from '@playwright/test';

test('create a Watch with a Script, Run now, and see the saved content', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('Name', { exact: true }).fill('Widget price');
  await page.getByLabel('Notification URL').fill('https://example.com/widget');
  await page.getByLabel('Script', { exact: true }).setInputFiles({
    name: 'price.ts',
    mimeType: 'text/plain',
    buffer: Buffer.from("export default { async run() { return { success: true, content: '£19.99' }; } };"),
  });
  await page.getByRole('button', { name: 'Create Watch' }).click();
  const watch = page.getByRole('article', { name: 'Widget price' });
  await expect(watch).toContainText('No Runs yet');
  await watch.getByRole('button', { name: 'Run now' }).click();
  await expect(watch).toContainText('£19.99');
  await expect(watch).toContainText('Succeeded');
  await page.reload();
  await expect(watch).toContainText('£19.99');
  await expect(watch).toContainText('In stock');
});
