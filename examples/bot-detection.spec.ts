import * as pw from 'playwright';
import { test } from 'vitest';
import { BotDetectionScript } from './bot-detection.js';

test('scrape function works correctly', async () => {
  const browser = await pw.chromium.launch({
    headless: false,
    args: ['--disable-blink-features=AutomationControlled'],
  });
  const page = await browser.newPage();

  const result = await new BotDetectionScript().run(page);
})
