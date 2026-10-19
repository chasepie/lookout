import type { LookoutScript } from '@chasepie/lookout-scripting';
import { setTimeout as wait } from 'node:timers/promises';

export default {
  async run() {
    void Promise.reject(new Error('Rejected in the background'));
    await wait(5_000);
    return { success: true };
  },
} satisfies LookoutScript;
