/** A JavaScript Script, which the runner must accept as well as TypeScript */
/** @type {import('@chasepie/lookout-scripting').LookoutScript} */
export default {
  async run(page, context) {
    await page.goto(context.notificationUrl);
    return { success: true, content: await page.locator('.price').innerText() };
  },
};
