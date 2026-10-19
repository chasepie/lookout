import type { LookoutScript } from '@chasepie/lookout-scripting';

/** Logs far more than the runner keeps, numbering each line */
export default {
  run() {
    for (let line = 1; line <= 20_000; line++) {
      console.log(`line ${line} ${'x'.repeat(50)}`);
    }
    return Promise.resolve({ success: true });
  },
} satisfies LookoutScript;
