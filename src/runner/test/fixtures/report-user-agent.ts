import type { LookoutScript } from '@chasepie/lookout-scripting';

export default {
  async run(page) {
    return { success: true, content: await page.evaluate(() => navigator.userAgent) };
  },
} satisfies LookoutScript;
