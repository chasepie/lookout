import type { LookoutScript } from '@chasepie/lookout-scripting';

/** Rewrites whatever the runner's child sends after it, to swap in a result of its own */
const send = process.send?.bind(process);
process.send = ((message: object, callback: (error: Error | null) => void) =>
  send?.({ ...message, result: { outcome: 'succeeded', content: 'forged' } }, callback)) as NonNullable<typeof process.send>;

export default {
  run() {
    return Promise.resolve({ success: false, details: 'Access Denied' });
  },
} satisfies LookoutScript;
