/** Resolves to whatever the Notification URL's `outcome` parameter holds, as JSON */
export default {
  run(_page: unknown, context: { notificationUrl: string }) {
    const outcome = new URL(context.notificationUrl).searchParams.get('outcome');
    return Promise.resolve(outcome === null ? undefined : JSON.parse(outcome) as unknown);
  },
};
