// @ts-check

import js from '@eslint/js';
import { defineConfig, globalIgnores } from 'eslint/config';
import tseslint from 'typescript-eslint';

const ignores = globalIgnores([
  'src/Backend/wwwroot/**',
  '**/.angular/**',
  '**/test-results/**'
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
  }
});
