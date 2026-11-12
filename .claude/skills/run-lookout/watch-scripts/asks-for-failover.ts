import type { LookoutWatchScript, RunResult } from "@chasepie/lookout-watch-sdk";
import type { Page } from "playwright";

// A known failure that asks for a failover, after holding the session for 10 seconds
export default class implements LookoutWatchScript {
  async run(page: Page, signal: AbortSignal): Promise<RunResult> {
    await page.goto('https://ipinfo.io/json', { signal });
    const info = JSON.parse(await page.locator('body').innerText({ signal })) as { ip?: string; country?: string };
    await page.waitForTimeout(10_000);
    return { success: false, failureReason: `Pretending ${info.country} is blocked (${info.ip})`, failover: true };
  }
}
