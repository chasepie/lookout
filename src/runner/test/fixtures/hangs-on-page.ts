import type { LookoutScript } from '@chasepie/lookout-scripting';

export default {
  async run(page) {
    await page.setContent('<h1>Loading…</h1>');
    return await new Promise(() => undefined);
  },
} satisfies LookoutScript;
