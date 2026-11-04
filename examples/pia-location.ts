import type { LookoutWatchScript, RunResult } from "@chasepie/lookout-watch-sdk";
import type { Page } from "playwright";

export default class implements LookoutWatchScript {
  async run(page: Page, signal: AbortSignal): Promise<RunResult> {
    await page.goto('https://www.privateinternetaccess.com/what-is-my-ip', { signal });

    const locElement = page
      .locator('.card-info-row', { hasText: /^Location.*/ })
      .locator('.exposed-info span:nth-of-type(2)')
    await locElement.waitFor({ signal });

    const innerText = await locElement.innerText({ signal });
    return { success: true, content: innerText.trim() };
  }
}
