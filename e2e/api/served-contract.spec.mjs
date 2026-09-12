// E2E spec for R1-01: On-disk contract freeze (served artifacts).
// Proves that /install.sh and /skill.md are served anonymously with correct Content-Type headers,
// that <POINTER_SERVER> origin injection runs (no literal placeholder remains),
// and that the served install.sh contains the frozen names and the un-ignored config.json gitignore line.
// Contract: docs/roadmap/testing/R1-01-tests.md
// Tier: PR
import { test, expect } from '@playwright/test';
import { readFileSync, existsSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { raw, getRaw } from '../scripts/lib/api.mjs';
import { record } from '../scripts/lib/report.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '..', '..');
const diskInstallPath = resolve(root, 'API', 'wwwroot', 'install.sh');
const diskSkillPath = resolve(root, 'API', 'wwwroot', 'skill.md');

/**
 * Produces a human-readable line-by-line diff between served content and disk content.
 */
function formatDiff(label, servedText, diskText) {
  if (servedText === diskText) return `--- ${label}: served matches disk exactly ---`;
  const servedLines = (servedText || '').split('\n');
  const diskLines = (diskText || '').split('\n');
  const max = Math.max(servedLines.length, diskLines.length);
  const diffs = [];
  for (let i = 0; i < max; i++) {
    const s = servedLines[i];
    const d = diskLines[i];
    if (s !== d) {
      diffs.push(`  line ${i + 1}:\n    served: ${s !== undefined ? JSON.stringify(s) : '<EOF>'}\n    disk:   ${d !== undefined ? JSON.stringify(d) : '<EOF>'}`);
      if (diffs.length >= 10) {
        diffs.push(`  ... and ${max - i - 1} more differing lines`);
        break;
      }
    }
  }
  return `--- ${label} diff (served vs disk) ---\n${diffs.join('\n')}`;
}

test('R1-01-01 — served-install-sh-contract', async () => {
  const start = Date.now();

  let installRes;
  let skillRes;

  try {
    // 1. GET http://localhost:8090/install.sh (no Authorization header).
    // raw() returns { status, ok, headers, body, text, isSuccess, data } without throwing.
    installRes = await raw('GET', '/install.sh');
    expect(installRes.status, 'GET /install.sh must return 200').toBe(200);

    // Header content-type === text/x-shellscript; charset=utf-8 (Program.cs injected-files branch)
    const installContentType = installRes.headers?.get('content-type') || '';
    expect(installContentType, 'Content-Type header for install.sh must be text/x-shellscript; charset=utf-8').toBe(
      'text/x-shellscript; charset=utf-8'
    );

    const installBody = installRes.text ?? (typeof installRes.body === 'string' ? installRes.body : '');

    // Body contains the gitignore line !.pointer/config.json (AC-3 on the served artifact)
    expect(installBody, 'Served install.sh must contain gitignore line !.pointer/config.json').toContain(
      '!.pointer/config.json'
    );

    // Body contains the frozen names .pointer/ and !.pointer/stack.json
    expect(installBody, 'Served install.sh must contain frozen name .pointer/').toContain('.pointer/');
    expect(installBody, 'Served install.sh must contain frozen name !.pointer/stack.json').toContain(
      '!.pointer/stack.json'
    );

    // Body does not contain <POINTER_SERVER> (origin rewrite ran)
    expect(installBody, 'Served install.sh must not contain <POINTER_SERVER> placeholder').not.toContain(
      '<POINTER_SERVER>'
    );

    // 2. GET http://localhost:8090/skill.md (no Authorization header).
    skillRes = await raw('GET', '/skill.md');
    expect(skillRes.status, 'GET /skill.md must return 200').toBe(200);

    // Header content-type === text/markdown; charset=utf-8
    const skillContentType = skillRes.headers?.get('content-type') || '';
    expect(skillContentType, 'Content-Type header for skill.md must be text/markdown; charset=utf-8').toBe(
      'text/markdown; charset=utf-8'
    );

    const skillBody = skillRes.text ?? (typeof skillRes.body === 'string' ? skillRes.body : '');

    // Body contains .pointer/credentials.env
    expect(skillBody, 'Served skill.md must contain .pointer/credentials.env').toContain(
      '.pointer/credentials.env'
    );

    // Body does not contain <POINTER_SERVER>
    expect(skillBody, 'Served skill.md must not contain <POINTER_SERVER> placeholder').not.toContain(
      '<POINTER_SERVER>'
    );

    // 3. Grep both bodies for the literal placeholder <POINTER_SERVER> — zero matches in both.
    const installMatches = (installBody.match(/<POINTER_SERVER>/g) || []).length;
    const skillMatches = (skillBody.match(/<POINTER_SERVER>/g) || []).length;
    expect(installMatches, 'install.sh must have zero occurrences of <POINTER_SERVER>').toBe(0);
    expect(skillMatches, 'skill.md must have zero occurrences of <POINTER_SERVER>').toBe(0);

    const durationMs = Date.now() - start;
    record({
      id: 'R1-01-01',
      tier: 'PR',
      layer: 'api',
      role: '—',
      result: 'PASS',
      ms: durationMs,
      detail: 'served install.sh and skill.md contract verified (Content-Type, frozen names, no <POINTER_SERVER>)',
    });
  } catch (err) {
    // Evidence: bodies diffed against API/wwwroot/ on failure
    try {
      if (existsSync(diskInstallPath) && installRes) {
        const diskInstall = readFileSync(diskInstallPath, 'utf8');
        const installText = installRes.text ?? (typeof installRes.body === 'string' ? installRes.body : '');
        console.error(formatDiff('install.sh', installText, diskInstall));
      }
      if (existsSync(diskSkillPath) && skillRes) {
        const diskSkill = readFileSync(diskSkillPath, 'utf8');
        const skillText = skillRes.text ?? (typeof skillRes.body === 'string' ? skillRes.body : '');
        console.error(formatDiff('skill.md', skillText, diskSkill));
      }
    } catch {
      // Ignore diff logging errors to preserve original assertion error
    }
    throw err;
  }
});
