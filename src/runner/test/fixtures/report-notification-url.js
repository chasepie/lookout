export default {
  async run(page, context) {
    await page.setContent(`<h1>${context.notificationUrl}</h1>`);
    return { success: true, content: await page.textContent('h1') };
  },
};
