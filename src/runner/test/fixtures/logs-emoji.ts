import type { LookoutScript } from '@chasepie/lookout-scripting';

/** Logs more than the runner keeps of characters that each take two UTF-16 code units */
export default {
  run() {
    console.log('😀'.repeat(150_000));
    return Promise.resolve({ success: true });
  },
} satisfies LookoutScript;
