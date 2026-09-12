// e2e/landing/claims.spec.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { FORBIDDEN_CLAIMS } from './forbidden-claims.mjs';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, '../..');
const landingDir = path.join(repoRoot, 'landing');
const indexPath = path.join(landingDir, 'index.html');

test('R3-06-08: v2 retired and no forbidden claim shipped', async (t) => {
  // 1. Assert landing/v2/ does not exist
  const v2Path = path.join(landingDir, 'v2');
  assert.equal(fs.existsSync(v2Path), false, 'landing/v2/ must not exist');

  // 2. grep -ri "v2/" landing/ -> no references
  function checkNoV2Refs(dir) {
    const entries = fs.readdirSync(dir, { withFileTypes: true });
    for (const ent of entries) {
      const full = path.join(dir, ent.name);
      if (ent.isDirectory()) {
        checkNoV2Refs(full);
      } else if (ent.isFile() && (ent.name.endsWith('.html') || ent.name.endsWith('.js') || ent.name.endsWith('.json'))) {
        const content = fs.readFileSync(full, 'utf8');
        assert.ok(!content.includes('v2/'), `File ${full} must not reference v2/`);
      }
    }
  }
  checkNoV2Refs(landingDir);

  // 3. Extract rendered text from landing/index.html
  const indexHtml = fs.readFileSync(indexPath, 'utf8');
  
  // Strip <style> blocks and check user-visible text (HTML body and STRINGS dictionaries)
  const noStyle = indexHtml.replace(/<style[\s\S]*?<\/style>/gi, '');
  
  // Extract STRINGS dictionary
  const stringsMatch = indexHtml.match(/var STRINGS = (\{[\s\S]*?\n  \};)/);
  assert.ok(stringsMatch, 'STRINGS dictionary must be present in index.html');
  
  // Combine all user-facing copy: dictionary strings + body text
  let renderedText = noStyle;
  try {
    const fn = new Function(`${stringsMatch[0]}; return STRINGS;`);
    const stringsObj = fn();
    renderedText += ' ' + Object.values(stringsObj.en || {}).join(' ') + ' ' + Object.values(stringsObj.ar || {}).join(' ');
  } catch (e) {
    // fallback
  }
  
  // Test each forbidden pattern individually
  const resultsTable = [];
  let violations = 0;

  for (const claim of FORBIDDEN_CLAIMS) {
    const match = renderedText.match(claim.pattern);
    const passed = !match;
    if (!passed) {
      violations++;
    }
    resultsTable.push({
      id: claim.id,
      label: claim.label,
      blockedOn: claim.blockedOn,
      status: passed ? 'PASS' : 'FAIL',
      match: match ? match[0] : 'none'
    });
  }

  // Log per-pattern table
  console.table(resultsTable);
  assert.equal(violations, 0, `Expected zero forbidden claims, but found ${violations}`);

  // 4. Assert token section exists but carries no numeral-plus-% or numeral-plus-tokens construction
  assert.ok(indexHtml.includes('why.f3'), 'Token feature key why.f3 must exist');
  assert.ok(!/why\.f3.*?\d+%/i.test(indexHtml), 'why.f3 must not carry numeral-plus-%');
  assert.ok(!/why\.f3.*?\d[\d,]*\s*tokens/i.test(indexHtml), 'why.f3 must not carry numeral-plus-tokens');
});
