import type { LookoutScript } from '@chasepie/lookout-scripting';
import type { Page } from 'playwright';

const sleep = (ms: number) => new Promise(
  (resolve) => setTimeout(resolve, ms));

export class BotDetectionScript implements LookoutScript {
  async run(page: Page) {
    await page.goto('https://www.browserscan.net/bot-detection');
    await sleep(20000);
    return { success: true };
  }
}
