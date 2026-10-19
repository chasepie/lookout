import type { LookoutScript } from '@chasepie/lookout-scripting';

export default {
  run() {
    process.kill(process.pid, 'SIGKILL');
    return Promise.resolve({ success: true });
  },
} satisfies LookoutScript;
