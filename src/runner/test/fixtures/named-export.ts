import type { LookoutScript } from '@chasepie/lookout-scripting';

export const script = {
  run() {
    return Promise.resolve({ success: true });
  },
} satisfies LookoutScript;
