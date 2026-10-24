import { readFile, writeFile } from 'node:fs/promises';
import openapiTS, { astToString } from 'openapi-typescript';

const schema = new URL('../../../../deploy/contracts/admin-api.v1.json', import.meta.url);
const output = new URL('../src/api/contracts.generated.ts', import.meta.url);
const generated = astToString(await openapiTS(schema, { alphabetize: true }));
if (process.argv.includes('--write')) {
  await writeFile(output, generated);
  console.log('Generated API types');
} else {
  const current = await readFile(output, 'utf8');
  if (current.replaceAll('\r\n', '\n') !== generated.replaceAll('\r\n', '\n'))
    throw new Error('API types differ from OpenAPI; run pnpm generate:api');
  console.log('PASS: frontend API types match OpenAPI');
}
