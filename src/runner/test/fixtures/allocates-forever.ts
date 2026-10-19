import type { LookoutScript } from '@chasepie/lookout-scripting';

export default {
  run() {
    const hoard: object[][] = [];
    for (;;) {
      hoard.push(new Array<object>(100_000).fill({}));
    }
  },
} satisfies LookoutScript;
