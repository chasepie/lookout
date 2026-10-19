import type { LookoutScript } from '@chasepie/lookout-scripting';
import { devices } from 'playwright';

export default {
  run() {
    return Promise.resolve({ success: true, content: devices['Desktop Chrome'].defaultBrowserType });
  },
} satisfies LookoutScript;
