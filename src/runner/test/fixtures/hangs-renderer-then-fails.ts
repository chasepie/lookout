import type { LookoutScript } from '@chasepie/lookout-scripting';

/** Reports a Known failure straight after hanging its page's renderer, so that no screenshot of the page can finish */
export default {
  async run(page) {
    await page.setContent('<h1>Access Denied</h1>');
    void page.evaluate(() => {
      for (;;) {
        // Spins forever
      }
    });
    return { success: false, details: 'Access Denied' };
  },
} satisfies LookoutScript;
