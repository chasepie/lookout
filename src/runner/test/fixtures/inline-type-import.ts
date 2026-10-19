// The inline form of a type import, which type stripping leaves as `import {} from '@chasepie/lookout-scripting'`
import { type LookoutScript } from '@chasepie/lookout-scripting';

export default {
  run() {
    return Promise.resolve({ success: true, content: 'imported' });
  },
} satisfies LookoutScript;
