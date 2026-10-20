// @ts-check

import { defineConfig } from "eslint/config";
import baseConfig from '../../eslint.config.mjs';
import angular from 'angular-eslint';


// const eslint = require('@eslint/js');
// const { defineConfig } = require('eslint/config');
// const tseslint = require('typescript-eslint');
// const angular = require('angular-eslint');

export default defineConfig([
  {
    files: ['**/*.ts'],
    extends: [
      baseConfig,
      angular.configs.tsRecommended,
    ],
    processor: angular.processInlineTemplates,
    rules: {
      '@angular-eslint/directive-selector': [
        'error',
        {
          type: 'attribute',
          prefix: 'app',
          style: 'camelCase',
        },
      ],
      '@angular-eslint/component-selector': [
        'error',
        {
          type: 'element',
          prefix: 'app',
          style: 'kebab-case',
        },
      ],
    },
  },
  {
    files: ['**/*.html'],
    extends: [
      angular.configs.templateRecommended,
      angular.configs.templateAccessibility
    ],
    rules: {},
  }
]);
