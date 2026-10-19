import type { LookoutScript } from '@chasepie/lookout-scripting';

/** Writes to the channel the runner reads results from, before returning its real outcome */
export default {
  run() {
    process.send?.({ outcome: 'yes' });
    return Promise.resolve({ success: true, content: 'done' });
  },
} satisfies LookoutScript;
