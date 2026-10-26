import { expect, test } from '@playwright/test';
import { randomUUID } from 'node:crypto';
import { changeTimeZone } from './watches';

/** The wall-clock time of an instant in a time zone, as hh:mm. */
function timeIn(instant: string, timeZone: string) {
  return new Intl.DateTimeFormat('en-GB', { timeZone, hour: '2-digit', minute: '2-digit', hourCycle: 'h23' })
    .format(new Date(instant));
}

// The time zone is install-wide, so this runs after the other tests (see playwright.config.ts), which expect the test
// host's own time zone, and puts it back for later runs against the same backend.
test('change the time zone in Settings and see a daily Watch run at its time there', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('link', { name: 'Settings', exact: true }).click();
  const settings = page.getByRole('region', { name: 'Time zone' });
  const timeZone = settings.getByLabel('Time zone', { exact: true });
  await expect(timeZone).not.toHaveValue('');
  const original = await timeZone.inputValue();
  const chosen = original === 'Asia/Kathmandu' ? 'Pacific/Chatham' : 'Asia/Kathmandu';

  try {
    await timeZone.fill('Mars/Olympus_Mons');
    await settings.getByRole('button', { name: 'Save' }).click();
    await expect(page.getByRole('alert')).toHaveText(
      'Lookout doesn\'t know a time zone called \'Mars/Olympus_Mons\'. Enter an IANA time zone, such as Europe/London.');
    await page.reload();
    await expect(timeZone).toHaveValue(original);

    const name = `Daily in ${chosen} ${randomUUID()}`;
    await page.goto('/');
    await page.getByLabel('Name', { exact: true }).fill(name);
    await page.getByLabel('Notification URL').fill('https://example.com/widget');
    await page.getByLabel('Script', { exact: true }).setInputFiles({
      name: 'price.ts', mimeType: 'text/plain', buffer: Buffer.from('export default {}'),
    });
    await page.getByLabel('Schedule', { exact: true }).selectOption('Daily at a set time');
    await expect(page.getByText(`In Lookout's time zone, ${original}.`)).toBeVisible();
    await page.getByLabel('Time of day').fill('07:45');
    await page.getByRole('button', { name: 'Create Watch' }).click();
    const nextRun = page.getByRole('article', { name }).getByText('Next Run').locator('time');
    expect(timeIn((await nextRun.getAttribute('datetime'))!, original)).toBe('07:45');

    await page.getByRole('link', { name: 'Settings', exact: true }).click();
    await timeZone.fill(chosen);
    await settings.getByRole('button', { name: 'Save' }).click();
    await expect(page.getByRole('status')).toHaveText(`Time zone saved. Daily and cron Watches now run in ${chosen}.`);
    await page.reload();
    await expect(timeZone).toHaveValue(chosen);

    await page.getByRole('link', { name: 'Lookout home' }).click();
    await expect.poll(async () => timeIn((await nextRun.getAttribute('datetime'))!, chosen)).toBe('07:45');
    expect(timeIn((await nextRun.getAttribute('datetime'))!, original)).not.toBe('07:45');
    await page.getByRole('article', { name }).getByRole('button', { name: 'Edit schedule' }).click();
    await expect(page.getByText(`In Lookout's time zone, ${chosen}.`)).toBeVisible();
  } finally {
    await changeTimeZone(page, original);
  }
});
