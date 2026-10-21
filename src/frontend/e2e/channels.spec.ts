import { expect, test } from '@playwright/test';
import { randomUUID } from 'node:crypto';
import { type IncomingMessage, createServer } from 'node:http';
import type { AddressInfo } from 'node:net';
import { createWatch } from './watches';

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

test('configure a Discord Channel and send it a test notification', async ({ page }) => {
  const webhook = await startFakeWebhook();
  try {
    const name = `Deals server ${randomUUID()}`;
    await page.goto('/');
    await page.getByRole('link', { name: 'Settings' }).click();
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Settings');

    await page.getByLabel('Name', { exact: true }).fill(name);
    await page.getByLabel('Webhook URL').fill(`${webhook.url}/api/webhooks/1/secret-token`);
    await page.getByRole('button', { name: 'Add Channel' }).click();
    await expect(page.getByRole('status')).toHaveText(`${name} added.`);

    const channel = page.getByRole('article', { name });
    await expect(channel).toContainText('Discord');
    await channel.getByRole('button', { name: 'Send test' }).click();
    await expect(page.getByRole('status')).toHaveText(`Test notification sent to ${name}.`);
    expect(webhook.received).toEqual([{
      path: '/api/webhooks/1/secret-token',
      body: expect.objectContaining({ embeds: [expect.objectContaining({ title: 'Lookout test notification' })] }) as unknown,
    }]);

    // The webhook URL holds its token, which the backend never sends back.
    await page.reload();
    await expect(channel).toBeVisible();
    await expect(page.getByText('secret-token')).toHaveCount(0);
  } finally {
    await webhook.close();
  }
});

test('add a Pushover Channel, see why its test notification failed, and delete it', async ({ page }) => {
  const name = `Phone ${randomUUID()}`;
  await page.goto('/settings');

  await page.getByLabel('Kind').selectOption('Pushover');
  await page.getByLabel('Name', { exact: true }).fill(name);
  await page.getByLabel('User key').fill('short');
  await page.getByLabel('App token').fill('azGDORePK8gMaC0QOYAMyEEuzJnyUi');
  await page.getByRole('button', { name: 'Add Channel' }).click();
  await expect(page.getByRole('alert')).toHaveText('Pushover\'s user key and app token are each 30 letters and digits.');

  await page.getByLabel('User key').fill('uQiRzpo4DXghDmr9QzzfQu27cmVRsG');
  await page.getByRole('button', { name: 'Add Channel' }).click();
  const channel = page.getByRole('article', { name });
  await expect(channel).toContainText('Pushover');

  // The test host sends Pushover's notifications to a local port nothing listens on, so this one fails.
  await channel.getByRole('button', { name: 'Send test' }).click();
  await expect(page.getByRole('alert')).toHaveText(/^Could not send the test notification: .+/);

  await channel.getByRole('button', { name: 'Delete' }).click();
  await channel.getByRole('button', { name: 'Delete permanently' }).click();
  await expect(page.getByRole('status')).toHaveText(`${name} deleted.`);
  await expect(channel).toHaveCount(0);
});

test('choose which Channels a Watch notifies, and see the choice kept after a reload', async ({ page }) => {
  const phone = `Phone ${randomUUID()}`;
  const server = `Deals server ${randomUUID()}`;
  await page.goto('/settings');
  for (const name of [phone, server]) {
    await page.getByLabel('Name', { exact: true }).fill(name);
    await page.getByLabel('Webhook URL').fill('https://discord.com/api/webhooks/1/token');
    await page.getByRole('button', { name: 'Add Channel' }).click();
    await expect(page.getByRole('status')).toHaveText(`${name} added.`);
  }
  const id = await createWatch(page, `Notifying ${randomUUID()}`);
  await page.goto(`/watches/${id}`);
  const channels = page.getByRole('region', { name: 'Channels' });

  await channels.getByLabel(phone).check();
  await channels.getByRole('button', { name: 'Save Channels' }).click();
  await expect(page.getByRole('status')).toHaveText('Channels saved.');

  await page.reload();
  await expect(channels.getByLabel(phone)).toBeChecked();
  await expect(channels.getByLabel(server)).not.toBeChecked();
});
