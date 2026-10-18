import * as pw from 'playwright';
import { expect, test } from 'vitest';
import BotDetectionScript from './bot-detection.js';

test('bot-detection Script reports success', { timeout: 60_000 }, async () => {
  const browser = await pw.chromium.launch({
    headless: false,
    args: ['--disable-blink-features=AutomationControlled'],
  });

  try {
    const page = await browser.newPage();
    const result = await new BotDetectionScript().run(page, {
      notificationUrl: 'https://www.browserscan.net/bot-detection',
    });

    expect(result.success).toBe(true);
  } finally {
    await browser.close();
  }
});
