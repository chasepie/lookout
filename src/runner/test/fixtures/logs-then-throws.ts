import type { LookoutScript } from '@chasepie/lookout-scripting';

export default {
  async run(page) {
    await page.setContent('<h1>Widget</h1>');
    console.log('Looking for the price');
    throw new Error('The price element has moved');
  },
} satisfies LookoutScript;
