import type { LookoutScript } from '@chasepie/lookout-scripting';

const escapeHtml = (text: string) => text.replaceAll('&', '&amp;').replaceAll('<', '&lt;');

export default {
  async run(page, context) {
    await page.setContent(`<h1>${escapeHtml(context.notificationUrl)}</h1>`);
    return { success: true, content: await page.locator('h1').innerText() };
  },
} satisfies LookoutScript;
