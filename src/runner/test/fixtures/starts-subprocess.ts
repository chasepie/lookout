import type { LookoutScript } from '@chasepie/lookout-scripting';
import { spawn } from 'node:child_process';

/**
 * Starts a subprocess that would outlive the Run, with the Notification URL's `marker` parameter among its
 * arguments so a test can find it. With `inherit`, it shares the Script's stdout and stderr. Then the Script
 * Succeeds with the subprocess's pid or, with `hang`, never finishes.
 */
export default {
  async run(_page, context) {
    const options = new URL(context.notificationUrl).searchParams;
    const subprocess = spawn(process.execPath, ['-e', 'setTimeout(() => {}, 25_000)', options.get('marker') ?? ''], {
      stdio: options.has('inherit') ? 'inherit' : 'ignore',
    });
    if (options.has('hang')) {
      await new Promise(() => undefined);
    }
    return { success: true, content: String(subprocess.pid) };
  },
} satisfies LookoutScript;
