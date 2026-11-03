import { runWatch } from '@chasepie/lookout-watch-sdk';
import { expect, test } from 'vitest';
import BotDetectionScript from './bot-detection.js';

test('bot-detection Script reports a visible browser as not a bot',
  { timeout: 60_000 },
  async ({ signal }) => {
    const result = await runWatch(new BotDetectionScript(), { signal });
    expect(result).toEqual({
      success: true,
      content: 'Normal'
    });
  });
