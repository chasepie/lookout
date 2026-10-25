import { type Page, expect, test } from '@playwright/test';
import { randomUUID } from 'node:crypto';
import { type IncomingMessage, createServer } from 'node:http';
import type { AddressInfo } from 'node:net';
import { holdFirst } from './hold';
import { changeNotificationChannels, createNotificationChannel, createWatch } from './watches';

// For Notification channels no test sends to: a local port nothing listens on (the discard port), so nothing reaches Discord.
const unreachableWebhook = 'http://127.0.0.1:9/api/webhooks/1/token';

/**
 * Waits for Settings to list its Notification channels. Its form is bound by then, so it keeps what is typed: until its
 * first render, the form writes its empty model over anything typed into it.
 */
async function settingsLoaded(page: Page) {
  const notificationChannels = page.getByRole('region', { name: 'Notification channels' });
  await expect(notificationChannels.getByRole('article').or(notificationChannels.getByText('No Notification channels yet.')).first()).toBeVisible();
}

interface ReceivedRequest { path: string; body: unknown }

/** Starts a local endpoint standing in for a Discord webhook, which records what it is sent. */
async function startFakeWebhook() {
  const received: ReceivedRequest[] = [];
  const server = createServer((request: IncomingMessage, response) => {
    let body = '';
    request.setEncoding('utf8');
    request.on('data', (chunk: string) => { body += chunk; });
    request.on('end', () => {
      received.push({ path: request.url ?? '', body: JSON.parse(body) as unknown });
      response.writeHead(204).end();
    });
  });
  await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
  const { port } = server.address() as AddressInfo;
  return { received, url: `http://127.0.0.1:${port}`, close: () => new Promise((resolve) => server.close(resolve)) };
}

test('configure a Discord Notification channel and send it a test notification', async ({ page }) => {
  const webhook = await startFakeWebhook();
  try {
    const name = `Deals server ${randomUUID()}`;
    await page.goto('/');
    await page.getByRole('link', { name: 'Settings', exact: true }).click();
    await settingsLoaded(page);
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Settings');

    await page.getByLabel('Name', { exact: true }).fill(name);
    await page.getByLabel('Webhook URL').fill(`${webhook.url}/api/webhooks/1/secret-token`);
    await page.getByRole('button', { name: 'Add Notification channel' }).click();
    await expect(page.getByRole('status')).toHaveText(`${name} added.`);

    const notificationChannel = page.getByRole('article', { name });
    await expect(notificationChannel).toContainText('Discord');
    await notificationChannel.getByRole('button', { name: 'Send test' }).click();
    await expect(page.getByRole('status')).toHaveText(`Test notification sent to ${name}.`);
    expect(webhook.received).toEqual([{
      path: '/api/webhooks/1/secret-token',
      body: expect.objectContaining({ embeds: [expect.objectContaining({ title: 'Lookout test notification' })] }) as unknown,
    }]);

    // The webhook URL holds its token, which the backend never sends back.
    await page.reload();
    await expect(notificationChannel).toBeVisible();
    await expect(page.getByText('secret-token')).toHaveCount(0);
  } finally {
    await webhook.close();
  }
});

test('add a Pushover Notification channel, see why its test notification failed, and delete it', async ({ page }) => {
  const name = `Phone ${randomUUID()}`;
  await page.goto('/settings');
  await settingsLoaded(page);

  await page.getByLabel('Kind').selectOption('Pushover');
  await page.getByLabel('Name', { exact: true }).fill(name);
  await page.getByLabel('User key').fill('short');
  await page.getByLabel('App token').fill('azGDORePK8gMaC0QOYAMyEEuzJnyUi');
  await page.getByRole('button', { name: 'Add Notification channel' }).click();
  await expect(page.getByRole('alert')).toHaveText('Pushover\'s user key and app token are each 30 letters and digits.');

  await page.getByLabel('User key').fill('uQiRzpo4DXghDmr9QzzfQu27cmVRsG');
  await page.getByRole('button', { name: 'Add Notification channel' }).click();
  const notificationChannel = page.getByRole('article', { name });
  await expect(notificationChannel).toContainText('Pushover');

  // The test host sends Pushover's notifications to a local port nothing listens on, so this one fails.
  await notificationChannel.getByRole('button', { name: 'Send test' }).click();
  await expect(page.getByRole('alert')).toHaveText(/^Could not send the test notification: .+/);

  await notificationChannel.getByRole('button', { name: 'Delete' }).click();
  await notificationChannel.getByRole('button', { name: 'Delete permanently' }).click();
  await expect(page.getByRole('status')).toHaveText(`${name} deleted.`);
  await expect(notificationChannel).toHaveCount(0);
});

test('create a Watch that notifies a Notification channel', async ({ page }) => {
  const notificationChannel = `Phone ${randomUUID()}`;
  const watch = `Notifying from the start ${randomUUID()}`;
  await page.goto('/settings');
  await settingsLoaded(page);
  await page.getByLabel('Name', { exact: true }).fill(notificationChannel);
  await page.getByLabel('Webhook URL').fill(unreachableWebhook);
  await page.getByRole('button', { name: 'Add Notification channel' }).click();
  await expect(page.getByRole('status')).toHaveText(`${notificationChannel} added.`);

  await page.getByRole('link', { name: 'Lookout home' }).click();
  // The form writes its empty model into fields typed in before its first render; the Notification channels it loads come after.
  const choice = page.getByRole('group', { name: 'Notification channels to notify' }).getByLabel(notificationChannel);
  await expect(choice).toBeVisible();
  await page.getByLabel('Name', { exact: true }).fill(watch);
  await page.getByLabel('Notification URL').fill('https://example.com/widget');
  await page.getByLabel('Script', { exact: true }).setInputFiles({
    name: 'price.ts', mimeType: 'text/plain', buffer: Buffer.from('export default {}'),
  });
  await choice.check();
  await page.getByRole('button', { name: 'Create Watch' }).click();

  await page.getByRole('article', { name: watch }).getByRole('link', { name: watch }).click();
  await expect(page.getByRole('region', { name: 'Notification channels' }).getByLabel(notificationChannel)).toBeChecked();
});

test('choose which Notification channels a Watch notifies, and see the choice kept after a reload', async ({ page }) => {
  const phone = `Phone ${randomUUID()}`;
  const server = `Deals server ${randomUUID()}`;
  await page.goto('/settings');
  await settingsLoaded(page);
  for (const name of [phone, server]) {
    await page.getByLabel('Name', { exact: true }).fill(name);
    await page.getByLabel('Webhook URL').fill(unreachableWebhook);
    await page.getByRole('button', { name: 'Add Notification channel' }).click();
    await expect(page.getByRole('status')).toHaveText(`${name} added.`);
  }
  const id = await createWatch(page, `Notifying ${randomUUID()}`);
  await page.goto(`/watches/${id}`);
  const notificationChannels = page.getByRole('region', { name: 'Notification channels' });

  await notificationChannels.getByLabel(phone).check();
  await notificationChannels.getByRole('button', { name: 'Save Notification channels' }).click();
  await expect(page.getByRole('status')).toHaveText('Notification channels saved.');

  await page.reload();
  await expect(notificationChannels.getByLabel(phone)).toBeChecked();
  await expect(notificationChannels.getByLabel(server)).not.toBeChecked();
});

test.describe('when responses arrive late', () => {
  test.afterEach(async ({ page }) => {
    await page.unrouteAll({ behavior: 'ignoreErrors' });
  });

  for (const late of ['the save', 'the Watch read after it'] as const) {
    test(`Notification channel choices made while ${late} is pending are kept`, async ({ page }) => {
      const phone = `Phone ${randomUUID()}`;
      const server = `Deals server ${randomUUID()}`;
      await createNotificationChannel(page, phone);
      await createNotificationChannel(page, server);
      const id = await createWatch(page, `Late Notification channels ${randomUUID()}`);
      await page.goto(`/watches/${id}`);
      const notificationChannels = page.getByRole('region', { name: 'Notification channels' });
      await expect(notificationChannels.getByLabel(phone)).not.toBeChecked();
      const release = await holdFirst(page, late === 'the save' ? `**/api/watches/${id}/notification-channels` : `**/api/watches/${id}`);

      await notificationChannels.getByLabel(phone).check();
      await notificationChannels.getByRole('button', { name: 'Save Notification channels' }).click();
      if (late === 'the save') {
        await expect(notificationChannels.getByRole('button', { name: 'Saving…' })).toBeVisible();
      } else {
        await expect(page.getByRole('status')).toHaveText('Notification channels saved.');
      }
      await notificationChannels.getByLabel(server).check();
      const reread = page.waitForResponse((response) => response.request().method() === 'GET'
        && new URL(response.url()).pathname === `/api/watches/${id}`);
      release();
      await reread;

      await expect(page.getByRole('status')).toHaveText('Notification channels saved.');
      await expect(notificationChannels.getByLabel(phone)).toBeChecked();
      await expect(notificationChannels.getByLabel(server)).toBeChecked();
    });
  }

  test('a choice changed elsewhere after a save is shown, however it changes next', async ({ page }) => {
    const phone = `Phone ${randomUUID()}`;
    const server = `Deals server ${randomUUID()}`;
    const phoneId = await createNotificationChannel(page, phone);
    const serverId = await createNotificationChannel(page, server);
    const id = await createWatch(page, `Changed elsewhere ${randomUUID()}`);
    await page.goto(`/watches/${id}`);
    const notificationChannels = page.getByRole('region', { name: 'Notification channels' });
    await expect(notificationChannels.getByLabel(phone)).not.toBeChecked();
    const release = await holdFirst(page, `**/api/watches/${id}/notification-channels`);

    // Another tab clears the choice after this page's save, before this page reads it back.
    await notificationChannels.getByLabel(phone).check();
    await notificationChannels.getByRole('button', { name: 'Save Notification channels' }).click();
    await expect(notificationChannels.getByRole('button', { name: 'Saving…' })).toBeVisible();
    await changeNotificationChannels(page, id, []);
    const reread = page.waitForResponse((response) => response.request().method() === 'GET'
      && new URL(response.url()).pathname === `/api/watches/${id}`);
    release();
    await reread;

    await changeNotificationChannels(page, id, [serverId]);
    await page.getByRole('button', { name: 'Pause' }).click();
    await expect(notificationChannels.getByLabel(server)).toBeChecked();
    await expect(notificationChannels.getByLabel(phone)).not.toBeChecked();
    // Back to the choice this page saved, which must not be mistaken for its save arriving.
    await changeNotificationChannels(page, id, [phoneId]);
    await page.getByRole('button', { name: 'Resume' }).click();
    await expect(notificationChannels.getByLabel(phone)).toBeChecked();
    await expect(notificationChannels.getByLabel(server)).not.toBeChecked();
  });
});
