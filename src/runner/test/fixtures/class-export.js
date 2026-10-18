export default class {
  async run(_page, context) {
    return { success: true, content: `class ran for ${context.notificationUrl}` };
  }
}
