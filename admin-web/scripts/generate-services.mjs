/**
 * Downloads the OpenAPI/Swagger spec from the running API server
 * and saves it as openapi.json, then runs orval to generate
 * Angular services and models.
 *
 * Prerequisites:
 *   - The .NET API must be running (default: http://localhost:8090)
 *     e.g. `just up` or `dotnet run --project API`
 *
 * Usage:
 *   npm run generate-services
 */

import { writeFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execSync } from 'node:child_process';

const __dirname = dirname(fileURLToPath(import.meta.url));
const SPEC_URL = process.env.POINTER_SWAGGER_URL ?? 'http://localhost:8090/swagger/v1/swagger.json';
const OUTPUT_PATH = resolve(__dirname, '..', 'openapi.json');

console.log(`Fetching OpenAPI spec from ${SPEC_URL} ...`);

try {
  const response = await fetch(SPEC_URL);
  if (!response.ok) {
    throw new Error(`HTTP ${response.status} ${response.statusText}`);
  }
  const spec = await response.text();
  writeFileSync(OUTPUT_PATH, spec, 'utf-8');
  console.log(`Saved spec to ${OUTPUT_PATH} (${(spec.length / 1024).toFixed(1)} KB)`);
} catch (err) {
  console.error(`\nFailed to download spec from ${SPEC_URL}`);
  console.error('Make sure the API is running (e.g. `just up` or `dotnet run --project API`).');
  console.error(`Error: ${err.message}\n`);
  process.exit(1);
}

console.log('Running orval ...');

try {
  execSync('npx orval --config ./orval.config.ts', {
    cwd: resolve(__dirname, '..'),
    stdio: 'inherit',
  });
  console.log('Done! Generated services are in src/app/core/api/generated/');
} catch {
  process.exit(1);
}
