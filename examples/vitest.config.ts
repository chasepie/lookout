import { defaultServerConditions } from 'vite';
import { defineConfig } from 'vitest/config';

export default defineConfig({
  ssr: {
    resolve: {
      // Resolve workspace packages (e.g. @chasepie/lookout-watch-sdk) to their .ts source instead of dist/
      conditions: ['@chasepie/source', ...defaultServerConditions],
    },
  },
});
