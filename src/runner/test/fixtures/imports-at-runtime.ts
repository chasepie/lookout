import type { LookoutScript } from '@chasepie/lookout-scripting';

/** Imports the module named by the Notification URL's `specifier` parameter once it's running */
export default {
  async run(_page, context) {
    const specifier = new URL(context.notificationUrl).searchParams.get('specifier') ?? '';
    await import(specifier);
    return { success: true };
  },
} satisfies LookoutScript;
