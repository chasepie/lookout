import type { LookoutScript } from '@chasepie/lookout-scripting';

export default {
  async run(page) {
    await page.setContent('<h1>Access Denied</h1>');
    console.log('Looking for the price');
    return { success: false, details: 'Access Denied' };
  },
} satisfies LookoutScript;
