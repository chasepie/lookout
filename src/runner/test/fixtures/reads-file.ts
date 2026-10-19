import type { LookoutScript } from '@chasepie/lookout-scripting';
import { readFile } from 'node:fs/promises';

/** Reads the file at the Notification URL's `path` parameter, or its own file without one */
export default {
  async run(_page, context) {
    const path = new URL(context.notificationUrl).searchParams.get('path') ?? import.meta.filename;
    return { success: true, content: await readFile(path, 'utf8') };
  },
} satisfies LookoutScript;
