// @ts-check

import js from '@eslint/js';
import { defineConfig, globalIgnores } from 'eslint/config';
import tseslint from 'typescript-eslint';

export default defineConfig(
  // Build output, caches and local data, which ESLint would otherwise lint because it doesn't read .gitignore
  globalIgnores([
    '.docker-volumes/',
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
        tsconfigRootDir: import.meta.dirname,
      }
    }
  },
  {
    // node:test's describe and test return promises the runner itself awaits
    files: ['**/test/**/*.test.ts'],
    rules: {
      '@typescript-eslint/no-floating-promises': ['error', {
        allowForKnownSafeCalls: [{ from: 'package', package: 'node:test', name: ['describe', 'test'] }],
      }],
    },
  },
);
