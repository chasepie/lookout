import type { LookoutScript } from '@chasepie/lookout-scripting';
import { setTimeout as wait } from 'node:timers/promises';

export default {
  async run() {
    setTimeout(() => {
      throw new Error('Thrown from a timer');
    }, 0);
    await wait(5_000);
    return { success: true };
  },
} satisfies LookoutScript;
