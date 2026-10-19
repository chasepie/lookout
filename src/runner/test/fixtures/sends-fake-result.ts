import type { LookoutScript } from '@chasepie/lookout-scripting';

/**
 * Sends a well-formed result on the channel the runner reads results from, then returns its real outcome
 * or, when the Notification URL asks it to, never finishes
 */
export default {
  async run(_page, context) {
    process.send?.({ outcome: 'succeeded', content: 'early message' });
    if (new URL(context.notificationUrl).searchParams.has('hang')) {
      await new Promise(() => undefined);
    }
    return { success: false, details: 'Access Denied' };
  },
} satisfies LookoutScript;
