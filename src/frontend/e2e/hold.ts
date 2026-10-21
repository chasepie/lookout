import type { Page } from '@playwright/test';

/** Holds the first matching response until released, passing later ones straight through. */
export async function holdFirst(page: Page, url: string | RegExp) {
  let release!: () => void;
  const released = new Promise<void>((resolve) => { release = resolve; });
  let held = false;
  await page.route(url, async (route) => {
    if (held) {
      await route.continue();
      return;
    }
    held = true;
    // The app may abandon the request while it is held.
    try {
      const response = await route.fetch();
      await released;
      await route.fulfill({ response });
    } catch {
      // Abandoned: there is no one left to answer.
    }
  });
  return release;
}
