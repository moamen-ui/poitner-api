#!/usr/bin/env node
// Enforces the bundle gzip budget for widget.js:
// Fails with exit code 1 if files["widget.js"].gzipBytes > budget.widgetJsGzipMax.
// Usage:
//   node scripts/check-budget.mjs [path-to-pointer.version.json]
import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const versionFilePath = process.argv[2]
  ? resolve(process.cwd(), process.argv[2])
  : resolve(here, '../API/wwwroot/pointer.version.json');

try {
  const content = readFileSync(versionFilePath, 'utf8');
  const data = JSON.parse(content);
  const jsGzip = data.files?.['widget.js']?.gzipBytes;
  const maxGzip = data.budget?.widgetJsGzipMax ?? 65536;

  if (typeof jsGzip !== 'number') {
    console.error(`check-budget: widget.js gzipBytes missing or invalid in ${versionFilePath}`);
    process.exit(1);
  }

  if (jsGzip > maxGzip) {
    console.error(
      `check-budget ✘ widget.js gzip size ${jsGzip} B exceeds budget of ${maxGzip} B!`
    );
    process.exit(1);
  }

  console.log(`check-budget ✔ widget.js gzip size ${jsGzip} B is within budget of ${maxGzip} B`);
  process.exit(0);
} catch (err) {
  console.error(`check-budget error reading ${versionFilePath}: ${err.message}`);
  process.exit(1);
}
