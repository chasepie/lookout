import { type Page, expect } from '@playwright/test';

/** Creates a Watch through the API with the page's login, sending the request token the app would. */
export async function createWatch(page: Page, name: string, notificationUrl = 'https://example.com/widget') {
  const token = (await page.context().cookies()).find((cookie) => cookie.name === 'XSRF-TOKEN')?.value;
  expect(token, 'the saved login includes a request token').toBeDefined();
  const created = await page.request.post('/api/watches', {
    headers: { 'X-XSRF-TOKEN': token! },
    multipart: {
      name, notificationUrl,
      script: { name: 'price.ts', mimeType: 'text/plain', buffer: Buffer.from('export default {}') },
    },
  });
  expect(created.status()).toBe(201);
}
