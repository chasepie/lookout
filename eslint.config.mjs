// @ts-check

import js from '@eslint/js';
import { defineConfig } from 'eslint/config';
import tseslint from 'typescript-eslint';

export default defineConfig({
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
        allowDefaultProject: ['src/runner/test/fixtures/*.js']
      }
    }
  }
});
