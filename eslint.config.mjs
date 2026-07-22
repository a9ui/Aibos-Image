import { defineConfig, globalIgnores } from 'eslint/config';
import nextVitals from 'eslint-config-next/core-web-vitals';
import prettier from 'eslint-config-prettier/flat';
import testingLibrary from 'eslint-plugin-testing-library';

const testFiles = ['src/**/*.test.{ts,tsx}', 'src/**/__tests__/**/*.{ts,tsx}'];
const testingReact = testingLibrary.configs['flat/react'];
const testingDom = testingLibrary.configs['flat/dom'];

export default defineConfig([
  ...nextVitals,
  {
    rules: {
      'react-hooks/set-state-in-effect': 'off',
      'react-hooks/preserve-manual-memoization': 'off',
    },
  },
  {
    ...testingReact,
    files: testFiles,
  },
  {
    ...testingDom,
    files: testFiles,
  },
  prettier,
  globalIgnores([
    '.next/**',
    '.cache/**',
    '.agents/**',
    '.claude/**',
    '.codex/**',
    '.cursor/**',
    '.grok/**',
    '.playwright-cli/**',
    'coverage/**',
    'node_modules/**',
    'out/**',
    'build/**',
    'exports/**',
    'playwright-report/**',
    'test-results/**',
    'next-env.d.ts',
  ]),
]);
