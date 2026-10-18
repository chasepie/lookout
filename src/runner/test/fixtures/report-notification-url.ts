import type { LookoutScript } from '@chasepie/lookout-scripting';

export default {
  async run(page, context) {
    await page.setContent('<h1></h1>');
    await page.locator('h1').evaluate((h1, url) => { h1.textContent = url; }, context.notificationUrl);
    return { success: true, content: await page.locator('h1').innerText() };
  },
} satisfies LookoutScript;
