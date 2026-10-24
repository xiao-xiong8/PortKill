import vue from 'eslint-plugin-vue';
import ts from 'typescript-eslint';

export default ts.config(
  { ignores: ['dist/**', 'node_modules/**', 'src/api/contracts.generated.ts', 'test-results/**', 'playwright-report/**'] },
  ...ts.configs.recommended,
  ...vue.configs['flat/essential'],
  { files: ['**/*.vue'], languageOptions: { parserOptions: { parser: ts.parser } } },
  { languageOptions: { globals: { process: 'readonly', console: 'readonly', URL: 'readonly' } } },
);
