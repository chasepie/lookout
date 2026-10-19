import type { LookoutScript } from '@chasepie/lookout-scripting';

/** Leaves a wait pending, which rejects when the runner closes the browser after the Script has finished */
export default {
  async run(page) {
    await page.setContent('<p class="price">£20.00</p>');
    void page.waitForSelector('.banner');
    return { success: true, content: await page.locator('.price').innerText() };
  },
} satisfies LookoutScript;
