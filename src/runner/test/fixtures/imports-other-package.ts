import type { LookoutScript } from '@chasepie/lookout-scripting';
// Installed alongside the runner, but not one of the packages a Script may import
import { expect } from 'vitest';

export default {
  run() {
    expect(true).toBe(true);
    return Promise.resolve({ success: true });
  },
} satisfies LookoutScript;
