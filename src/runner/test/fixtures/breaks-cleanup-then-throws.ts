import type { LookoutScript } from '@chasepie/lookout-scripting';

/** Makes the runner's cleanup fail after the Script's own error */
export default {
  run(page) {
    const browser = page.context().browser();
    if (browser) {
      browser.close = () => Promise.reject(new Error('Closing the browser failed'));
    }
    throw new Error('The price element has moved');
  },
} satisfies LookoutScript;
