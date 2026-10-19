import type { LookoutScript } from '@chasepie/lookout-scripting';

export default {
  async run(page, context) {
    await page.goto(context.notificationUrl);
    const heading = await page.locator('h1').innerText();
    if (heading === 'Access Denied') {
      return { success: false, details: heading };
    }
    return { success: true, content: await page.locator('.price').innerText() };
  },
} satisfies LookoutScript;
