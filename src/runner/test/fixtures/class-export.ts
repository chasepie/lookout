import type { LookoutScript, ScriptContext } from '@chasepie/lookout-scripting';
import type { Page } from 'playwright';

export default class implements LookoutScript {
  run(_page: Page, context: ScriptContext) {
    return Promise.resolve({ success: true, content: `class ran for ${context.notificationUrl}` });
  }
}
