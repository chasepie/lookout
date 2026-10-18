import type { LookoutScript, ScriptContext } from '@chasepie/lookout-scripting';
import type { Page } from 'playwright';

const sleep = (ms: number) => new Promise(
  (resolve) => setTimeout(resolve, ms));

export default class BotDetectionScript implements LookoutScript {
  async run(page: Page, context: ScriptContext) {
    await page.goto(context.notificationUrl);
    await sleep(20000);
    return { success: true };
  }
}
