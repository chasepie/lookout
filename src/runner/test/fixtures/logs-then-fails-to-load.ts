import type { LookoutScript } from '@chasepie/lookout-scripting';

export default {
  run() {
    return Promise.resolve({ success: true });
  },
} satisfies LookoutScript;

// More than a pipe holds at once, so it's still being written when the Script fails
console.log('x'.repeat(1024 * 1024));
console.log('LAST-LINE');
throw new Error('Failed to load');
