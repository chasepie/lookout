import type { LookoutScript } from '@chasepie/lookout-scripting';
import { spawn } from 'node:child_process';

/** Starts a process that shares its stdout and stderr and outlives it, then never finishes */
export default {
  async run() {
    spawn(process.execPath, ['-e', 'setTimeout(() => {}, 25_000)'], { stdio: 'inherit' });
    return await new Promise(() => undefined);
  },
} satisfies LookoutScript;
