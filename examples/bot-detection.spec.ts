import { chromium } from 'playwright';
import { expect, test } from 'vitest';
import BotDetectionScript from './bot-detection.js';

test('bot-detection Script reports a visible browser as not a bot',
  { timeout: 60_000 },
  async ({ signal }) => {
    const browser = await chromium.launch({
      headless: false,
      args: ['--disable-blink-features=AutomationControlled'],
    });

    try {
      const page = await browser.newPage();
      const result = await new BotDetectionScript().run(page, signal);

      expect(result).toEqual({ success: true, content: 'Normal' });
    } finally {
      await browser.close();
    }
  });
