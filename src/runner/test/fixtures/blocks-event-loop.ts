import type { LookoutScript } from '@chasepie/lookout-scripting';

/** Never yields, so nothing in its process can run again: no timer, no IPC message */
export default {
  run() {
    for (;;) {
      // Spins forever
    }
  },
} satisfies LookoutScript;
