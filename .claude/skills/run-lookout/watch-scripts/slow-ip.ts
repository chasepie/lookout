import type { LookoutWatchScript, RunResult } from "@chasepie/lookout-watch-sdk";
import type { Page } from "playwright";

// Looks up where the browser's traffic comes from, then holds the session for 25 seconds, so runs overlap
export default class implements LookoutWatchScript {
  async run(page: Page, signal: AbortSignal): Promise<RunResult> {
    const started = new Date().toISOString();
    await page.goto('https://ipinfo.io/json', { signal });
    const info = JSON.parse(await page.locator('body').innerText({ signal })) as { ip?: string; country?: string; city?: string };
    await page.waitForTimeout(25_000);
    return { success: true, content: `${info.ip} ${info.city}, ${info.country} (started ${started}, ended ${new Date().toISOString()})` };
  }
}
