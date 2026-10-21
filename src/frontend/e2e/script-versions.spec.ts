import { expect, test } from '@playwright/test';
import { randomUUID } from 'node:crypto';
import { createWatch, runWatch } from './watches';

const newScript = "export default { async run(page) { return { success: true, content: await page.title() }; } };\n";

test('upload a new Script version, see its Test run, save it, and view its source', async ({ page }) => {
  const id = await createWatch(page, `New version ${randomUUID()}`);
  await runWatch(page, id, 1);
  await page.goto(`/watches/${id}`);
  await expect(page.getByText('Healthy', { exact: true })).toBeVisible();

  await page.getByRole('link', { name: 'Upload new version' }).click();
  await page.getByLabel('Script', { exact: true }).setInputFiles({
    name: 'price.ts', mimeType: 'text/plain', buffer: Buffer.from(newScript),
  });

  const testRun = page.getByRole('region', { name: 'Test run' });
  await expect(testRun).toContainText('Succeeded');
  await expect(testRun).toContainText('£19.99');
  await expect(testRun.getByRole('img', { name: 'Screenshot of the Script\'s page' })).toBeVisible();
  await expect(page.getByLabel('Keep Baseline')).not.toBeChecked();
  await page.getByRole('button', { name: 'Save new version' }).click();

  await expect(page).toHaveURL(`/watches/${id}`);
  await expect(page.getByText('Pending', { exact: true })).toBeVisible();
  const versions = page.getByRole('list', { name: 'Script versions' }).getByRole('listitem');
  await expect(versions).toHaveCount(2);
  await expect(versions.nth(0)).toContainText('Version 2');
  await expect(versions.nth(0)).toContainText('Current');
  await expect(versions.nth(1)).toContainText('Version 1');
  await expect(versions.nth(1)).not.toContainText('Current');
  await versions.nth(0).getByRole('button', { name: 'View source' }).click();
  await expect(versions.nth(0).getByRole('code')).toHaveText(newScript);
  await versions.nth(1).getByRole('button', { name: 'View source' }).click();
  await expect(versions.nth(1).getByRole('code')).toHaveText('export default {}');

  const runs = page.getByRole('list', { name: 'Run history' }).getByRole('listitem');
  await expect(runs.nth(0)).toContainText('Version 1');
  await page.getByRole('button', { name: 'Run now' }).click();
  await expect(runs).toHaveCount(2);
  await expect(runs.nth(0)).toContainText('Version 2');
  await expect(page.getByText('Healthy', { exact: true })).toBeVisible();
});

test('a Test run leaves the Watch as it was, and keeping the Baseline keeps its status', async ({ page }) => {
  const id = await createWatch(page, `Kept Baseline ${randomUUID()}`);
  await runWatch(page, id, 1);
  await page.goto(`/watches/${id}/script`);

  await page.getByLabel('Script', { exact: true }).setInputFiles({
    name: 'price.js', mimeType: 'text/plain', buffer: Buffer.from(newScript),
  });
  await expect(page.getByRole('region', { name: 'Test run' })).toContainText('Succeeded');
  await page.getByRole('button', { name: 'Test again' }).click();
  await expect(page.getByRole('region', { name: 'Test run' })).toContainText('Succeeded');
  await page.getByRole('link', { name: 'Back to Watch' }).click();

  await expect(page.getByRole('list', { name: 'Script versions' }).getByRole('listitem')).toHaveCount(1);
  await expect(page.getByRole('list', { name: 'Run history' }).getByRole('listitem')).toHaveCount(1);
  await page.getByRole('link', { name: 'Upload new version' }).click();
  await page.getByLabel('Script', { exact: true }).setInputFiles({
    name: 'price.js', mimeType: 'text/plain', buffer: Buffer.from(newScript),
  });
  await page.getByLabel('Keep Baseline').check();
  await page.getByRole('button', { name: 'Save new version' }).click();

  await expect(page).toHaveURL(`/watches/${id}`);
  await expect(page.getByRole('list', { name: 'Script versions' }).getByRole('listitem')).toHaveCount(2);
  await expect(page.getByText('Healthy', { exact: true })).toBeVisible();
});
