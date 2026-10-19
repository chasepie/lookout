import type { LookoutScript } from '@chasepie/lookout-scripting';

/** Leaves the runner no browser to screenshot or close after the Script's own error */
export default {
  async run(page) {
    await page.context().browser()?.close();
    throw new Error('The price element has moved');
  },
} satisfies LookoutScript;
