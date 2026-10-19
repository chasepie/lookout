import type { LookoutScript } from '@chasepie/lookout-scripting';

export default {
  run() {
    setInterval(() => undefined, 1_000);
    return Promise.resolve({ success: true, content: 'done' });
  },
} satisfies LookoutScript;
