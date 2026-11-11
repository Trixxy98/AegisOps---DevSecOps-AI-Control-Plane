// ESLint 9 flat config. Conventions: docs/18-repository-structure-and-conventions.md §3
// (typescript-eslint, react-hooks, jsx-a11y, Prettier). Formatting is owned by Prettier;
// eslint-config-prettier is applied last so no stylistic rule can fight it.
import js from "@eslint/js";
import prettier from "eslint-config-prettier";
import jsxA11y from "eslint-plugin-jsx-a11y";
import reactHooks from "eslint-plugin-react-hooks";
import reactRefresh from "eslint-plugin-react-refresh";
import { defineConfig, globalIgnores } from "eslint/config";
import globals from "globals";
import tseslint from "typescript-eslint";

export default defineConfig([
  globalIgnores(["dist", "coverage", "node_modules", "playwright-report", "test-results", "blob-report"]),
  {
    files: ["**/*.{ts,tsx}"],
    extends: [
      js.configs.recommended,
      // Type-aware rules: the project is small enough that a full run stays around two seconds.
      tseslint.configs.recommendedTypeChecked,
      reactHooks.configs.flat.recommended,
      reactRefresh.configs.vite,
      jsxA11y.flatConfigs.recommended,
    ],
    languageOptions: {
      ecmaVersion: 2022,
      globals: globals.browser,
      parserOptions: {
        projectService: {
          // e2e specs and root config files are outside tsconfig.json's "src" include.
          allowDefaultProject: ["e2e/*.ts", "e2e-live/*.ts", "*.config.ts"],
          // Default limit is 8; the e2e, e2e-live and config files together exceed it.
          maximumDefaultProjectFileMatchCount_THIS_WILL_SLOW_DOWN_LINTING: 16,
        },
        tsconfigRootDir: import.meta.dirname,
      },
    },
  },
  {
    files: ["eslint.config.js"],
    extends: [js.configs.recommended],
    languageOptions: {
      globals: globals.node,
    },
  },
  {
    files: ["e2e/**/*.ts", "e2e-live/**/*.ts", "*.config.ts"],
    languageOptions: {
      globals: globals.node,
    },
  },
  prettier,
]);
