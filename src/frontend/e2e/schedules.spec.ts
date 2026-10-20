import { type Page, expect, test } from '@playwright/test';
import { randomUUID } from 'node:crypto';
import { createWatch } from './watches';

async function fillWatch(page: Page, name: string) {
  await page.goto('/');
  await page.getByLabel('Name', { exact: true }).fill(name);
  await page.getByLabel('Notification URL').fill('https://example.com/widget');
  await page.getByLabel('Script', { exact: true }).setInputFiles({
    name: 'price.ts', mimeType: 'text/plain', buffer: Buffer.from('export default {}'),
  });
}

test('create a Watch that runs every few hours and see its next Run', async ({ page }) => {
  const name = `Every two hours ${randomUUID()}`;
  await fillWatch(page, name);
  await page.getByLabel('Every', { exact: true }).fill('2');
  await page.getByLabel('Unit').selectOption('hours');
  await page.getByLabel('Jitter (minutes)').fill('5');

  const before = Date.now();
  await page.getByRole('button', { name: 'Create Watch' }).click();
  const watch = page.getByRole('article', { name });
  const nextRun = watch.getByText('Next Run').locator('time');
  await expect(nextRun).toBeVisible();
  const at = Date.parse((await nextRun.getAttribute('datetime'))!);
  expect(at).toBeGreaterThanOrEqual(before + 2 * 3_600_000 - 1_000);
  expect(at).toBeLessThanOrEqual(Date.now() + 2 * 3_600_000 + 5 * 60_000 + 1_000);
});

test('create a Watch with an advanced cron schedule and see its next Run', async ({ page }) => {
  const name = `Weekday mornings ${randomUUID()}`;
  await fillWatch(page, name);
  await page.getByLabel('Schedule', { exact: true }).selectOption('Advanced (cron)');
  await page.getByLabel('Cron expression').fill('30 9 * * 1-5');

  await page.getByRole('button', { name: 'Create Watch' }).click();

  const nextRun = page.getByRole('article', { name }).getByText('Next Run').locator('time');
  await expect(nextRun).toBeVisible();
  // The test host runs in the browser's time zone, so the browser sees the Run at 09:30 on a weekday.
  const at = new Date((await nextRun.getAttribute('datetime'))!);
  expect([at.getHours(), at.getMinutes()]).toEqual([9, 30]);
  expect(at.getDay()).not.toBe(0);
  expect(at.getDay()).not.toBe(6);
});

test('create a Watch that runs daily at a set time', async ({ page }) => {
  const name = `Daily ${randomUUID()}`;
  await fillWatch(page, name);
  await page.getByLabel('Schedule', { exact: true }).selectOption('Daily at a set time');
  await page.getByLabel('Time of day').fill('07:45');

  await page.getByRole('button', { name: 'Create Watch' }).click();

  const nextRun = page.getByRole('article', { name }).getByText('Next Run').locator('time');
  const at = new Date((await nextRun.getAttribute('datetime'))!);
  expect([at.getHours(), at.getMinutes()]).toEqual([7, 45]);
});

test('explains that a picker schedule under 3 minutes is too frequent', async ({ page }) => {
  await fillWatch(page, `Too often ${randomUUID()}`);
  await page.getByLabel('Every', { exact: true }).fill('2');
  await page.getByLabel('Unit').selectOption('minutes');
  await page.getByLabel('Every', { exact: true }).blur();

  await expect(page.getByText('Schedules must not run more than once every 3 minutes.')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Create Watch' })).toBeDisabled();
});

test('shows why the backend rejected a cron schedule', async ({ page }) => {
  await fillWatch(page, `Too often cron ${randomUUID()}`);
  await page.getByLabel('Schedule', { exact: true }).selectOption('Advanced (cron)');
  await page.getByLabel('Cron expression').fill('0,1 9 * * *');

  await page.getByRole('button', { name: 'Create Watch' }).click();

  await expect(page.getByRole('alert')).toHaveText('Schedules must not run more than once every 3 minutes.');
});

test('edit a Watch\'s schedule, then see it kept after a reload', async ({ page }) => {
  const name = `Edited schedule ${randomUUID()}`;
  await createWatch(page, name);
  await page.goto('/');
  const watch = page.getByRole('article', { name });

  await watch.getByRole('button', { name: 'Edit schedule' }).click();
  await expect(watch.getByLabel('Every', { exact: true })).toHaveValue('1');
  await expect(watch.getByLabel('Unit')).toHaveValue('hours');
  await watch.getByLabel('Schedule', { exact: true }).selectOption('Advanced (cron)');
  await watch.getByLabel('Cron expression').fill('30 9 * * 1-5');
  await watch.getByLabel('Jitter (minutes)').fill('0');
  await watch.getByRole('button', { name: 'Save schedule' }).click();

  await expect(page.getByRole('status')).toHaveText(`Schedule of ${name} saved.`);
  await expect(watch.getByRole('button', { name: 'Save schedule' })).toHaveCount(0);
  const nextRun = watch.getByText('Next Run').locator('time');
  await expect.poll(async () => new Date((await nextRun.getAttribute('datetime'))!).getMinutes()).toBe(30);
  expect(new Date((await nextRun.getAttribute('datetime'))!).getHours()).toBe(9);

  await page.reload();
  await watch.getByRole('button', { name: 'Edit schedule' }).click();
  await expect(watch.getByLabel('Schedule', { exact: true })).toHaveValue('cron');
  await expect(watch.getByLabel('Cron expression')).toHaveValue('30 9 * * 1-5');
});

test('keeps a Watch\'s schedule when an edit is rejected', async ({ page }) => {
  const name = `Rejected edit ${randomUUID()}`;
  await createWatch(page, name);
  await page.goto('/');
  const watch = page.getByRole('article', { name });
  const nextRun = watch.getByText('Next Run').locator('time');
  const before = await nextRun.getAttribute('datetime');

  await watch.getByRole('button', { name: 'Edit schedule' }).click();
  await watch.getByLabel('Schedule', { exact: true }).selectOption('Advanced (cron)');
  await watch.getByLabel('Cron expression').fill('*/2 * * * *');
  await watch.getByRole('button', { name: 'Save schedule' }).click();

  await expect(watch.getByRole('alert')).toHaveText('Schedules must not run more than once every 3 minutes.');
  await watch.getByRole('button', { name: 'Cancel' }).click();
  await page.reload();
  await expect(nextRun).toHaveAttribute('datetime', before!);
});

test('edit a Watch to run daily with jitter, then see both kept after a reload', async ({ page }) => {
  const name = `Edited daily ${randomUUID()}`;
  await createWatch(page, name);
  await page.goto('/');
  const watch = page.getByRole('article', { name });

  await watch.getByRole('button', { name: 'Edit schedule' }).click();
  await watch.getByLabel('Schedule', { exact: true }).selectOption('Daily at a set time');
  await watch.getByLabel('Time of day').fill('07:45');
  await watch.getByLabel('Jitter (minutes)').fill('10');
  await watch.getByRole('button', { name: 'Save schedule' }).click();
  await expect(page.getByRole('status')).toHaveText(`Schedule of ${name} saved.`);

  // The Run lands up to 10 minutes after 07:45 in the browser's time zone, which the test host shares.
  const nextRun = watch.getByText('Next Run').locator('time');
  await expect.poll(async () => {
    const at = new Date((await nextRun.getAttribute('datetime'))!);
    return at.getHours() * 60 + at.getMinutes();
  }).toBeGreaterThanOrEqual(7 * 60 + 45);
  const at = new Date((await nextRun.getAttribute('datetime'))!);
  expect(at.getHours() * 60 + at.getMinutes()).toBeLessThanOrEqual(7 * 60 + 55);

  await page.reload();
  await watch.getByRole('button', { name: 'Edit schedule' }).click();
  await expect(watch.getByLabel('Schedule', { exact: true })).toHaveValue('daily');
  await expect(watch.getByLabel('Time of day')).toHaveValue('07:45');
  await expect(watch.getByLabel('Jitter (minutes)')).toHaveValue('10');
});
