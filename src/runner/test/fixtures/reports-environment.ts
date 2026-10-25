import type { LookoutScript } from '@chasepie/lookout-scripting';

/** Reports the environment variable named by the Notification URL's `name` parameter, or `unset` */
export default {
  run(_page, context) {
    const name = new URL(context.notificationUrl).searchParams.get('name') ?? '';
    return Promise.resolve({ success: true, content: process.env[name] ?? 'unset' });
  },
} satisfies LookoutScript;
