import { type Page, expect, test } from '@playwright/test';
import { randomUUID } from 'node:crypto';
import { createWatch, runWatch } from './watches';

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

test.describe('when responses arrive late', () => {
  test.afterEach(async ({ page }) => {
    await page.unrouteAll({ behavior: 'ignoreErrors' });
  });

  /** Holds the first matching response until released, passing later ones straight through. */
  async function holdFirst(page: Page, url: string | RegExp) {
    let release!: () => void;
    const released = new Promise<void>((resolve) => { release = resolve; });
    let held = false;
    await page.route(url, async (route) => {
      if (held) {
        await route.continue();
        return;
      }
      held = true;
      // The app may abandon the request while it is held.
      try {
        const response = await route.fetch();
        await released;
        await route.fulfill({ response });
      } catch {
        // Abandoned: there is no one left to answer.
      }
    });
    return release;
  }

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
