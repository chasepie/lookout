// @ts-check

import js from '@eslint/js';
import { defineConfig, globalIgnores } from 'eslint/config';
import tseslint from 'typescript-eslint';

export default defineConfig(
  // Build output, caches and local data, which ESLint would otherwise lint because it doesn't read .gitignore
  globalIgnores([
    '.shared-volumes/',
    '**/dist/',
    '**/coverage/',
    'coverage-report/',
    '**/bin/',
    '**/obj/',
    'src/frontend/.angular/',
    'src/Backend/wwwroot/',
  ]),
  {
    files: ['**/*.{js,ts}'],
    extends: [
      js.configs.recommended,
      tseslint.configs.recommendedTypeChecked,
      tseslint.configs.stylisticTypeChecked,
    ],
    languageOptions: {
      parserOptions: {
        projectService: true,
      }
    }
  },
);
