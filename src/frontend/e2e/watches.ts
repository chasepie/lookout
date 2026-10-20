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

/** Runs a Watch through the API the given number of times, one Run after another. */
export async function runWatch(page: Page, id: string, times: number) {
  const headers = { 'X-XSRF-TOKEN': await requestToken(page) };
  for (let run = 0; run < times; run++) {
    const response = await page.request.post(`/api/watches/${id}/runs`, { headers });
    expect(response.status()).toBe(200);
  }
}
