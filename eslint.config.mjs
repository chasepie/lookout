// @ts-check

import js from '@eslint/js';
import { defineConfig, globalIgnores } from 'eslint/config';
import tseslint from 'typescript-eslint';

const ignores = globalIgnores([
  'src/Backend/wwwroot/**',
  '**/.angular/**',
  '**/test-results/**',
  // The scripting package's build, which only lasts while it's packed, unless packing stops partway
  'src/scripting/dist/**'
]);
export default defineConfig(ignores, {
  files: ['**/*.{js,ts}'],
  extends: [
    js.configs.recommended,
    tseslint.configs.recommendedTypeChecked,
    tseslint.configs.stylisticTypeChecked
  ],
  languageOptions: {
    parserOptions: {
      projectService: {
        // The runner must accept JavaScript Scripts, which the TypeScript project doesn't include
        allowDefaultProject: [
          'src/runner/test/fixtures/*.js',
          'src/frontend/playwright.config.ts'
        ]
      }
    }
  },
  rules: {
    curly: 'error'
  }
});
