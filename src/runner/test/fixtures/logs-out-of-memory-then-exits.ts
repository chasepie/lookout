import type { LookoutScript } from '@chasepie/lookout-scripting';

/** Writes what V8 writes when a process runs out of memory, then exits without having done so */
export default {
  run() {
    console.error('FATAL ERROR: Reached heap limit Allocation failed - JavaScript heap out of memory');
    process.exit(3);
  },
} satisfies LookoutScript;
