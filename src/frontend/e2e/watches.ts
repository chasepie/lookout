import { type Page, expect } from '@playwright/test';

/** The request token from the page's saved login, which the app sends with every change. */
async function requestToken(page: Page) {
  const token = (await page.context().cookies()).find((cookie) => cookie.name === 'Lookout.XSRF-TOKEN')?.value;
  expect(token, 'the saved login includes a request token').toBeDefined();
  return token!;
}

/** Creates a Watch through the API with the page's login, sending the request token the app would, and returns its ID. */
export async function createWatch(page: Page, name: string, notificationUrl = 'https://example.com/widget') {
  const created = await page.request.post('/api/watches', {
    headers: { 'X-XSRF-TOKEN': await requestToken(page) },
    multipart: {
      name, notificationUrl,
      script: { name: 'price.ts', mimeType: 'text/plain', buffer: Buffer.from('export default {}') },
      scheduleKind: 'interval', every: '1', unit: 'hours',
    },
  });
  expect(created.status()).toBe(201);
  return ((await created.json()) as { id: string }).id;
}

/** Edits a Watch's settings through the API, as another browser tab would. */
export async function editWatch(page: Page, id: string, settings: { name: string; notificationUrl: string; timeoutSeconds: number }) {
  const response = await page.request.put(`/api/watches/${id}`, {
    headers: { 'X-XSRF-TOKEN': await requestToken(page) },
    data: settings,
  });
  expect(response.status()).toBe(204);
}

/** Pauses a Watch through the API, as another browser tab would. */
export async function pauseWatch(page: Page, id: string) {
  const response = await page.request.post(`/api/watches/${id}/pause`, { headers: { 'X-XSRF-TOKEN': await requestToken(page) } });
  expect(response.status()).toBe(204);
}

/** Uploads a new Script version through the API, as another browser tab would. */
export async function uploadScript(page: Page, id: string, source: string) {
  const response = await page.request.post(`/api/watches/${id}/script-versions`, {
    headers: { 'X-XSRF-TOKEN': await requestToken(page) },
    multipart: { script: { name: 'price.ts', mimeType: 'text/plain', buffer: Buffer.from(source) } },
  });
  expect(response.status()).toBe(201);
}

/** Runs a Watch through the API the given number of times, one Run after another. */
export async function runWatch(page: Page, id: string, times: number) {
  const headers = { 'X-XSRF-TOKEN': await requestToken(page) };
  for (let run = 0; run < times; run++) {
    const response = await page.request.post(`/api/watches/${id}/runs`, { headers });
    expect(response.status()).toBe(200);
  }
}

/** Creates a Discord Channel through the API, posting to a local port nothing listens on, and returns its ID. */
export async function createChannel(page: Page, name: string) {
  const created = await page.request.post('/api/channels', {
    headers: { 'X-XSRF-TOKEN': await requestToken(page) },
    data: { name, kind: 'discord', webhookUrl: 'http://127.0.0.1:9/api/webhooks/1/token' },
  });
  expect(created.status()).toBe(201);
  return ((await created.json()) as { id: string }).id;
}

/** Changes which Channels a Watch notifies through the API, as another browser tab would. */
export async function changeChannels(page: Page, id: string, channelIds: string[]) {
  const response = await page.request.put(`/api/watches/${id}/channels`, {
    headers: { 'X-XSRF-TOKEN': await requestToken(page) },
    data: { channelIds },
  });
  expect(response.status()).toBe(204);
}
