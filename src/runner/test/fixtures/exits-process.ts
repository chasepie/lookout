import type { LookoutScript } from '@chasepie/lookout-scripting';

export default {
  run() {
    process.exit(3);
  },
} satisfies LookoutScript;
