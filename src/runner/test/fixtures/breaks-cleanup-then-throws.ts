import type { LookoutScript } from '@chasepie/lookout-scripting';

/** Makes the runner's cleanup fail after the Script's own error */
export default {
  run(page) {
    page.context().close = () => Promise.reject(new Error('Closing the browser context failed'));
    throw new Error('The price element has moved');
  },
} satisfies LookoutScript;
