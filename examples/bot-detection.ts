import type { LookoutScript, ScriptContext } from '@chasepie/lookout-scripting';
import type { Page } from 'playwright';

/** Reports browserscan.net's bot-detection verdict, e.g. "Normal" or "Robot" */
export default class BotDetectionScript implements LookoutScript {
  async run(page: Page, context: ScriptContext) {
    await page.goto(context.notificationUrl);

    const verdict = page.getByText('Test Results:', { exact: true })
      .locator('xpath=following-sibling::strong');
    await verdict.waitFor();

    return { success: true, content: (await verdict.innerText()).trim() };
  }
}
