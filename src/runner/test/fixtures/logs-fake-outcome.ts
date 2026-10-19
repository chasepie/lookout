import type { LookoutScript } from '@chasepie/lookout-scripting';

export default {
  run() {
    console.log(JSON.stringify({ outcome: 'errored', errorMessage: 'not from the runner' }));
    console.error('more noise');
    return Promise.resolve({ success: true, content: 'done' });
  },
} satisfies LookoutScript;
