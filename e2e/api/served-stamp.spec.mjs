// R2-03: every served skill and script carries the version stamp, and it agrees with /api/meta.
//
// The stamp is what lets `doctor` tell a developer their installed copy is older than the server's.
// If the middleware and MetaService ever resolved it differently, doctor would compare an installed
// stamp against a value nothing stamps — reporting every install stale, or none. R2-03-02 is the
// assertion that stops that.
import { test, expect } from '@playwright/test';
import { BASE_URL, getRaw } from '../scripts/lib/api.mjs';
import { record } from '../scripts/lib/report.mjs';

const SERVED = ['/skill.md', '/pointer-init.md', '/install.sh', '/pointer.sh'];
const STAMP = /pointer-skill-version:\s*([^\s>]+)/;

test('R2-03-01 — served stamps on all four files', async () => {
  const start = Date.now();
  const found = {};

  for (const path of SERVED) {
    const res = await getRaw(path);
    expect(res.status, `${path} must be served`).toBe(200);

    const body = String(res.text ?? res.body);
    const match = body.match(STAMP);
    expect(match, `${path} carries no pointer-skill-version stamp`).toBeTruthy();
    expect(match[1]).not.toBe('<POINTER_SKILL_VERSION>');

    // No unresolved placeholders anywhere in the file — a half-substituted file would ship
    // "<POINTER_SKILL_VERSION>" into a customer's repository.
    expect(body, `${path} has an unresolved placeholder`).not.toContain('<POINTER_SKILL_VERSION>');
    expect(body, `${path} has an unresolved server placeholder`).not.toContain('<POINTER_SERVER>');
    expect(body, `${path} has an unresolved product placeholder`).not.toContain('<POINTER_PRODUCT>');

    // PLACEMENT. The two .md skills open with YAML frontmatter that AI tools parse; a stamp before
    // it breaks that parse, which is why the stamp goes on the first line AFTER the closing ---.
    const lines = body.split('\n');
    if (path.endsWith('.md')) {
      expect(lines[0].trim(), `${path} must still start with frontmatter`).toBe('---');
      expect(lines[0]).not.toContain('pointer-skill-version');

      const close = lines.findIndex((l, i) => i > 0 && l.trim() === '---');
      expect(close, `${path} has no closing frontmatter delimiter`).toBeGreaterThan(0);
      expect(lines[close + 1]).toMatch(STAMP);
    } else {
      expect(lines[0].startsWith('#!'), `${path} must keep its shebang on line 1`).toBe(true);
      expect(lines[1]).toMatch(STAMP);
    }

    found[path] = match[1];
  }

  // All four agree — they are stamped from one resolver, not four.
  const distinct = [...new Set(Object.values(found))];
  expect(distinct, `served files disagree: ${JSON.stringify(found)}`).toHaveLength(1);

  record({ id: 'R2-03-01', tier: 'PR', layer: 'api', role: '—', result: 'PASS',
    ms: Date.now() - start, detail: `stamp=${distinct[0]} across ${SERVED.length} files` });
});

test('R2-03-02 — /api/meta.skillVersion equals the stamp', async () => {
  const start = Date.now();

  const meta = await getRaw('/api/meta');
  expect(meta.status).toBe(200);
  const skillVersion = meta.data?.skillVersion;
  expect(skillVersion, 'meta.skillVersion must be set').toBeTruthy();

  const served = String((await getRaw('/skill.md')).text ?? '').match(STAMP)?.[1];

  // The whole staleness feature rests on this equality: doctor reads the installed stamp and
  // compares it with meta.skillVersion. Two resolvers that drift make every install look stale.
  expect(served).toBe(skillVersion);

  record({ id: 'R2-03-02', tier: 'PR', layer: 'api', role: '—', result: 'PASS',
    ms: Date.now() - start, detail: `meta=${skillVersion} served=${served} @ ${BASE_URL}` });
});
