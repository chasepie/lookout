import type { LookoutScript } from '@chasepie/lookout-scripting';

export const script = {
  async run() {
    return { success: true };
  },
} satisfies LookoutScript;
