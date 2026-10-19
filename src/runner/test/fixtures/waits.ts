import type { LookoutScript } from '@chasepie/lookout-scripting';
import { setTimeout } from 'node:timers/promises';

export default {
  async run() {
    await setTimeout(500);
    return { success: true };
  },
} satisfies LookoutScript;
