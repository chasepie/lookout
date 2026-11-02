import type { LookoutWatchScript, RunResult } from "@chasepie/lookout-watch-sdk";
import type { Page } from "playwright";

export default class implements LookoutWatchScript {
  async run(page: Page, signal: AbortSignal): Promise<RunResult> {
    await page.goto('https://www.browserscan.net/bot-detection', { signal });

    const verdict = page
      .getByText('Test Results:', { exact: true })
      .locator('xpath=following-sibling::strong');
    await verdict.waitFor({ signal });

    const innerText = await verdict.innerText({ signal });
    return { success: true, content: innerText.trim() };
  }
}
