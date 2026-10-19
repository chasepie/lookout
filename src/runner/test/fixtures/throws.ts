import type { LookoutScript } from '@chasepie/lookout-scripting';

export default {
  run() {
    throw new Error('The price element has moved');
  },
} satisfies LookoutScript;
