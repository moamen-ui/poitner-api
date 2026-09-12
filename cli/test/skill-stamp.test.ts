import { test } from 'node:test';
import assert from 'node:assert/strict';
import { promises as fs } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { readStamp } from '../src/lib/skill-stamp.js';
import { skillFilesFor } from '../src/lib/skill-paths.js';

async function write(name: string, content: string): Promise<string> {
  const dir = await fs.mkdtemp(join(tmpdir(), 'pointer-stamp-'));
  const path = join(dir, name);
  await fs.writeFile(path, content, 'utf8');
  return path;
}

test('reads the stamp that follows a markdown file\'s frontmatter', async () => {
  const path = await write('SKILL.md', [
    '---',
    'name: pointer-feedback',
    'description: something',
    '---',
    '<!-- pointer-skill-version: 2026.09.12 -->',
    '',
    '# Heading',
  ].join('\n'));

  assert.equal(await readStamp(path), '2026.09.12');
});

test('locates the stamp by scanning, not by line number', async () => {
  // The frontmatter grows over time; a fixed line number would silently stop finding the stamp.
  const path = await write('SKILL.md', [
    '---',
    'name: pointer-feedback',
    'description: a much longer block',
    'allowed-tools: Bash, Read',
    'extra: another key',
    '---',
    '<!-- pointer-skill-version: 9.9.9 -->',
  ].join('\n'));

  assert.equal(await readStamp(path), '9.9.9');
});

test('REJECTS a stamp on line 1 of a markdown file', async () => {
  // This is the rule worth guarding. AI tools parse the YAML frontmatter block; anything before
  // it breaks that parse. Accepting such a file would bless an install the tool cannot read.
  const path = await write('SKILL.md', [
    '<!-- pointer-skill-version: 1.2.3 -->',
    '---',
    'name: pointer-feedback',
    '---',
  ].join('\n'));

  assert.equal(await readStamp(path), null);
});

test('reads the stamp on line 2 of a shell script', async () => {
  const path = await write('pointer.sh', [
    '#!/usr/bin/env bash',
    '# pointer-skill-version: 2026.09.12',
    'echo hi',
  ].join('\n'));

  assert.equal(await readStamp(path), '2026.09.12');
});

test('returns null for an unstamped file, a missing file, and an unknown type', async () => {
  const unstamped = await write('SKILL.md', ['---', 'name: x', '---', '', '# Heading'].join('\n'));
  assert.equal(await readStamp(unstamped), null);

  const shNoStamp = await write('pointer.sh', ['#!/bin/sh', 'echo hi'].join('\n'));
  assert.equal(await readStamp(shNoStamp), null);

  assert.equal(await readStamp('/nonexistent/nope.md'), null);

  const other = await write('notes.txt', '# pointer-skill-version: 1.0.0');
  assert.equal(await readStamp(other), null);
});

test('does not mistake a later mention in the prose for the stamp', async () => {
  const path = await write('SKILL.md', [
    '---',
    'name: x',
    '---',
    '',
    '# Heading',
    '',
    'The file carries a pointer-skill-version: 4.5.6 comment near the top.',
  ].join('\n'));

  assert.equal(await readStamp(path), null);
});

test('skillFilesFor maps each AI tool to the paths init actually wrote', () => {
  assert.deepEqual(skillFilesFor({ aiTool: 'claude-code' }), [
    '.claude/skills/pointer-init/SKILL.md',
    '.claude/skills/pointer-feedback/SKILL.md',
    '.pointer/pointer.sh',
  ]);

  assert.deepEqual(skillFilesFor({ aiTool: 'cursor' }), [
    '.cursor/rules/pointer-init.md',
    '.cursor/rules/pointer-feedback.md',
    '.pointer/pointer.sh',
  ]);

  // An unknown or absent tool falls back to the same layout installSkills uses.
  assert.deepEqual(skillFilesFor({ aiTool: 'something-else' }), [
    '.agents/pointer-init/SKILL.md',
    '.agents/pointer-feedback/SKILL.md',
    '.pointer/pointer.sh',
  ]);
  assert.deepEqual(skillFilesFor({}), [
    '.agents/pointer-init/SKILL.md',
    '.agents/pointer-feedback/SKILL.md',
    '.pointer/pointer.sh',
  ]);
});

test('a recorded skillsDir overrides the tool mapping', () => {
  assert.deepEqual(skillFilesFor({ aiTool: 'claude-code', skillsDir: 'custom/skills' }), [
    'custom/skills/pointer-init/SKILL.md',
    'custom/skills/pointer-feedback/SKILL.md',
    '.pointer/pointer.sh',
  ]);
});
