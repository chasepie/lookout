import { type Page, expect, test } from '@playwright/test';
import { randomUUID } from 'node:crypto';
import { holdFirst } from './hold';
import { createWatch, editWatch, runWatch } from './watches';

test('open a Watch, edit its settings, and see its Run history with a highlighted Change', async ({ page }) => {
  const name = `Changing price ${randomUUID()}`;
  await createWatch(page, name, 'https://example.com/changing');
  await page.goto('/');
  await page.getByRole('article', { name }).getByRole('link', { name }).click();

  await expect(page.getByRole('heading', { level: 1 })).toHaveText(name);
  await expect(page.getByText('Pending', { exact: true })).toBeVisible();
  const renamed = `Renamed ${randomUUID()}`;
  await page.getByLabel('Name', { exact: true }).fill(renamed);
  await page.getByLabel('Timeout (seconds)').fill('45');
  await page.getByRole('button', { name: 'Save settings' }).click();
  await expect(page.getByRole('status')).toHaveText('Settings saved.');
  await expect(page.getByRole('heading', { level: 1 })).toHaveText(renamed);

  await page.getByRole('button', { name: 'Run now' }).click();
  await expect(page.getByRole('status')).toHaveText('Run finished: Succeeded.');
  await page.getByRole('button', { name: 'Run now' }).click();
  await expect(page.getByRole('status')).toHaveText('Run finished: Succeeded. Change recorded.');

  const runs = page.getByRole('list', { name: 'Run history' }).getByRole('listitem');
  await expect(runs).toHaveCount(2);
  await expect(runs.nth(0)).toContainText('Change');
  await expect(runs.nth(1)).not.toContainText('Change');
  await expect(page.getByText('Healthy', { exact: true })).toBeVisible();

  await page.reload();
  await expect(page.getByLabel('Name', { exact: true })).toHaveValue(renamed);
  await expect(page.getByLabel('Timeout (seconds)')).toHaveValue('45');
  await expect(runs).toHaveCount(2);
});

test('edit a Watch\'s schedule from its detail page, and see no next Run while it is paused', async ({ page }) => {
  const id = await createWatch(page, `Detail schedule ${randomUUID()}`);
  await page.goto(`/watches/${id}`);
  const schedule = page.getByRole('region', { name: 'Schedule', exact: true });
  const nextRun = schedule.getByText('Next Run').locator('time');
  await expect(nextRun).toBeVisible();

  await schedule.getByRole('button', { name: 'Edit schedule' }).click();
  await schedule.getByLabel('Every', { exact: true }).fill('10');
  await schedule.getByLabel('Unit').selectOption('minutes');
  await schedule.getByRole('button', { name: 'Save schedule' }).click();

  await expect(page.getByRole('status')).toHaveText('Schedule saved.');
  await expect(schedule.getByRole('button', { name: 'Save schedule' })).toHaveCount(0);
  await expect.poll(async () => {
    const minutesAway = (new Date((await nextRun.getAttribute('datetime'))!).getTime() - Date.now()) / 60_000;
    return Math.round(minutesAway);
  }).toBe(10);

  await page.getByRole('button', { name: 'Pause' }).click();
  await expect(schedule).toContainText('No scheduled Runs while paused.');
  await page.getByRole('button', { name: 'Resume' }).click();
  await expect(nextRun).toBeVisible();
});

test('pause and resume a Watch', async ({ page }) => {
  const name = `Pausable ${randomUUID()}`;
  const id = await createWatch(page, name);
  await page.goto(`/watches/${id}`);

  await page.getByRole('button', { name: 'Pause' }).click();
  await expect(page.getByText('Paused', { exact: true })).toBeVisible();
  await page.getByRole('link', { name: 'All Watches' }).click();
  await expect(page.getByRole('article', { name }).getByText('Paused', { exact: true })).toBeVisible();

  await page.getByRole('article', { name }).getByRole('link', { name }).click();
  await page.getByRole('button', { name: 'Resume' }).click();
  await expect(page.getByText('Pending', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Pause' })).toBeVisible();
});

test('delete a Watch', async ({ page }) => {
  const name = `Deletable ${randomUUID()}`;
  const id = await createWatch(page, name);
  await page.goto(`/watches/${id}`);

  await page.getByRole('button', { name: 'Delete Watch' }).click();
  await page.getByRole('button', { name: 'Delete permanently' }).click();

  await expect(page).toHaveURL('/');
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Watches');
  await expect(page.getByRole('article', { name })).toHaveCount(0);
  await page.goto(`/watches/${id}`);
  await expect(page.getByRole('alert')).toHaveText('This Watch no longer exists.');
});

test('the Watches list shows Runs recorded while it was not on screen', async ({ page }) => {
  const name = `Run elsewhere ${randomUUID()}`;
  const id = await createWatch(page, name);
  await page.goto('/');
  const card = page.getByRole('article', { name });
  await expect(card).toContainText('No Runs yet');
  await card.getByRole('link', { name }).click();
  await expect(page.getByRole('heading', { level: 1 })).toHaveText(name);

  // Recorded the way a scheduled Run is, without this page knowing.
  await runWatch(page, id, 1);
  await page.getByRole('link', { name: 'All Watches' }).click();

  await expect(card).toContainText('Last Run:');
});

test('a timeout must be a whole number of seconds', async ({ page }) => {
  const id = await createWatch(page, `Fractional timeout ${randomUUID()}`);
  await page.goto(`/watches/${id}`);

  await page.getByLabel('Timeout (seconds)').fill('1.5');
  await page.getByLabel('Timeout (seconds)').blur();

  await expect(page.getByText('Enter a whole number of seconds between 1 and 600.')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Save settings' })).toBeDisabled();
});

test('a name of the longest length is accepted with surrounding spaces, which are trimmed', async ({ page }) => {
  const id = await createWatch(page, `Long name ${randomUUID()}`);
  await page.goto(`/watches/${id}`);
  const longest = `Long name ${randomUUID()}`.padEnd(200, 'x');

  await page.getByLabel('Name', { exact: true }).fill(` ${longest} `);
  await page.getByRole('button', { name: 'Save settings' }).click();

  await expect(page.getByRole('status')).toHaveText('Settings saved.');
  await expect(page.getByRole('heading', { level: 1 })).toHaveText(longest);
});

test('the settings form shows a name changed elsewhere back to one saved here', async ({ page }) => {
  const original = `Original ${randomUUID()}`;
  const id = await createWatch(page, original);
  await page.goto(`/watches/${id}`);
  const renamed = `Renamed ${randomUUID()}`;
  await page.getByLabel('Name', { exact: true }).fill(renamed);
  await page.getByRole('button', { name: 'Save settings' }).click();
  await expect(page.getByRole('status')).toHaveText('Settings saved.');

  await editWatch(page, id, { name: original, notificationUrl: 'https://example.com/widget', timeoutSeconds: 120 });
  await page.getByRole('button', { name: 'Pause' }).click();
  await expect(page.getByLabel('Name', { exact: true })).toHaveValue(original);
  await editWatch(page, id, { name: renamed, notificationUrl: 'https://example.com/widget', timeoutSeconds: 120 });
  await page.getByRole('button', { name: 'Resume' }).click();

  await expect(page.getByRole('heading', { level: 1 })).toHaveText(renamed);
  await expect(page.getByLabel('Name', { exact: true })).toHaveValue(renamed);
});

test.describe('when responses arrive late', () => {
  test.afterEach(async ({ page }) => {
    await page.unrouteAll({ behavior: 'ignoreErrors' });
  });

  const runHistory = (page: Page) => page.getByRole('list', { name: 'Run history' }).getByRole('listitem');

  test('a late Pause response does not undo settings saved meanwhile', async ({ page }) => {
    const id = await createWatch(page, `Late pause ${randomUUID()}`);
    const release = await holdFirst(page, `**/api/watches/${id}/pause`);
    await page.goto(`/watches/${id}`);

    await page.getByRole('button', { name: 'Pause' }).click();
    const renamed = `Renamed ${randomUUID()}`;
    await page.getByLabel('Name', { exact: true }).fill(renamed);
    await page.getByRole('button', { name: 'Save settings' }).click();
    await expect(page.getByRole('heading', { level: 1 })).toHaveText(renamed);
    release();

    await expect(page.getByText('Paused', { exact: true })).toBeVisible();
    await expect(page.getByRole('heading', { level: 1 })).toHaveText(renamed);
    await expect(page.getByLabel('Name', { exact: true })).toHaveValue(renamed);
  });

  test('edits typed while the settings are saving are kept', async ({ page }) => {
    const id = await createWatch(page, `Late save ${randomUUID()}`);
    await page.goto(`/watches/${id}`);
    await expect(page.getByLabel('Timeout (seconds)')).toHaveValue('120');
    const release = await holdFirst(page, `**/api/watches/${id}`);

    // The backend trims the name it saves, which must still count as this save arriving.
    const renamed = `Renamed ${randomUUID()}`;
    await page.getByLabel('Name', { exact: true }).fill(` ${renamed} `);
    await page.getByRole('button', { name: 'Save settings' }).click();
    await expect(page.getByRole('button', { name: 'Saving…' })).toBeVisible();
    await page.getByLabel('Timeout (seconds)').fill('45');
    release();

    await expect(page.getByRole('status')).toHaveText('Settings saved.');
    await expect(page.getByRole('heading', { level: 1 })).toHaveText(renamed);
    await expect(page.getByLabel('Timeout (seconds)')).toHaveValue('45');
  });

  test('a deleted Watch is gone from the list while the list reloads', async ({ page }) => {
    const name = `Deleted before reload ${randomUUID()}`;
    await createWatch(page, name);
    await page.goto('/');
    const card = page.getByRole('article', { name });
    await card.getByRole('link', { name }).click();
    await holdFirst(page, '**/api/watches');

    await page.getByRole('button', { name: 'Delete Watch' }).click();
    await page.getByRole('button', { name: 'Delete permanently' }).click();

    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Watches');
    await expect(card).toHaveCount(0);
  });

  test('a delete that finishes after moving to another Watch stays on that Watch', async ({ page }) => {
    const deleted = await createWatch(page, `Deleted later ${randomUUID()}`);
    const other = `Still open ${randomUUID()}`;
    const otherId = await createWatch(page, other);
    await page.goto(`/watches/${deleted}`);
    await expect(page.getByRole('button', { name: 'Delete Watch' })).toBeVisible();
    const release = await holdFirst(page, `**/api/watches/${deleted}`);

    await page.getByRole('button', { name: 'Delete Watch' }).click();
    await page.getByRole('button', { name: 'Delete permanently' }).click();
    await page.getByRole('link', { name: 'All Watches' }).click();
    await page.getByRole('article', { name: other }).getByRole('link', { name: other }).click();
    await expect(page.getByRole('heading', { level: 1 })).toHaveText(other);
    const deleteAnswered = page.waitForResponse((response) => response.request().method() === 'DELETE');
    release();
    await deleteAnswered;

    // Nothing signals a navigation that must not happen, so give it time to.
    await page.waitForTimeout(300);
    await expect(page).toHaveURL(`/watches/${otherId}`);
    await expect(page.getByRole('heading', { level: 1 })).toHaveText(other);
  });

  test('a Run finished before the history first loads still appears', async ({ page }) => {
    const id = await createWatch(page, `Late history ${randomUUID()}`);
    const release = await holdFirst(page, new RegExp(`/api/watches/${id}/runs\\?`));
    await page.goto(`/watches/${id}`);

    await page.getByRole('button', { name: 'Run now' }).click();
    await expect(page.getByRole('status')).toHaveText('Run finished: Succeeded.');
    release();

    await expect(runHistory(page)).toHaveCount(1);
  });

  test('older Runs load once per click, however late they arrive', async ({ page }) => {
    const id = await createWatch(page, `Late older Runs ${randomUUID()}`);
    await runWatch(page, id, 51);
    const release = await holdFirst(page, new RegExp(`/api/watches/${id}/runs\\?.*before=`));
    await page.goto(`/watches/${id}`);

    await page.getByRole('button', { name: 'Show older Runs' }).click();
    await expect(page.getByRole('button', { name: 'Loading older Runs…' })).toBeDisabled();
    release();

    await expect(runHistory(page)).toHaveCount(51);
    await expect(page.getByRole('button', { name: 'Show older Runs' })).toHaveCount(0);
  });

  test('older Runs that arrive after Run now are discarded rather than leaving a gap', async ({ page }) => {
    const id = await createWatch(page, `Gap ${randomUUID()}`);
    await runWatch(page, id, 51);
    const release = await holdFirst(page, new RegExp(`/api/watches/${id}/runs\\?.*before=`));
    await page.goto(`/watches/${id}`);
    await page.getByRole('button', { name: 'Show older Runs' }).click();

    await page.getByRole('button', { name: 'Run now' }).click();
    await expect(page.getByRole('status')).toHaveText('Run finished: Succeeded.');
    release();

    await expect(runHistory(page)).toHaveCount(50);
    await page.getByRole('button', { name: 'Show older Runs' }).click();
    await expect(runHistory(page)).toHaveCount(52);
  });

  test('older Runs belong to the Watch on screen after moving between Watches', async ({ page }) => {
    const first = await createWatch(page, `First ${randomUUID()}`);
    const secondName = `Second ${randomUUID()}`;
    const second = await createWatch(page, secondName);
    await runWatch(page, first, 51);
    await runWatch(page, second, 51);
    await page.goto(`/watches/${first}`);
    await expect(runHistory(page)).toHaveCount(50);
    await page.getByRole('link', { name: 'All Watches' }).click();
    await page.getByRole('article', { name: secondName }).getByRole('link', { name: secondName }).click();
    await expect(page).toHaveURL(`/watches/${second}`);
    await page.getByRole('button', { name: 'Show older Runs' }).click();
    await expect(runHistory(page)).toHaveCount(51);

    // Back to the first Watch's page without leaving the route, so the detail page is reused.
    await page.evaluate(() => { history.go(-2); });

    await expect(page).toHaveURL(`/watches/${first}`);
    await expect(runHistory(page)).toHaveCount(50);
    await expect(page.getByRole('button', { name: 'Show older Runs' })).toBeVisible();
  });
});
