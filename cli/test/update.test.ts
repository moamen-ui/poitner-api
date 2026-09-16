import { test } from 'node:test';
import assert from 'node:assert/strict';
import { promises as fs } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { createServer, type Server } from 'node:http';
import { updateCommand } from '../src/commands/update.js';

async function scratch(config: Record<string, unknown>): Promise<string> {
  const dir = await fs.mkdtemp(join(tmpdir(), 'pointer-update-'));
  await fs.mkdir(join(dir, '.pointer'), { recursive: true });
  await fs.writeFile(join(dir, '.pointer/config.json'), JSON.stringify(config), 'utf8');
  return dir;
}

/** A stub Pointer server: /api/meta reports a skill version, the three served files return fixed bodies. */
async function stubServer(skillVersion: string): Promise<{ url: string; close: () => Promise<void> }> {
  const server: Server = createServer((req, res) => {
    if (req.url === '/api/meta') {
      res.writeHead(200, { 'content-type': 'application/json' });
      res.end(JSON.stringify({ isSuccess: true, data: { skillVersion } }));
      return;
    }
    if (req.url === '/pointer.sh') {
      res.writeHead(200, { 'content-type': 'text/plain' });
      res.end(`#!/bin/sh\n# pointer-skill-version: ${skillVersion}\necho hi\n`);
      return;
    }
    if (req.url === '/pointer-init.md' || req.url === '/skill.md') {
      res.writeHead(200, { 'content-type': 'text/markdown' });
      res.end(`---\nname: x\n---\n<!-- pointer-skill-version: ${skillVersion} -->\n\nbody\n`);
      return;
    }
    res.writeHead(404, { 'content-type': 'application/json' });
    res.end(JSON.stringify({ isSuccess: false, message: 'not found' }));
  });
  await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
  const port = (server.address() as { port: number }).port;
  return {
    url: `http://127.0.0.1:${port}`,
    close: () => new Promise<void>((resolve) => server.close(() => resolve())),
  };
}

test('update installs pointer.sh and the skill files when they are entirely missing', async () => {
  const stub = await stubServer('2026.09.16');
  const dir = await scratch({ server: stub.url, project: 'demo', environment: 'local', aiTool: 'claude-code' });
  try {
    const code = await updateCommand(dir, { server: stub.url });
    assert.equal(code, 0);

    for (const rel of [
      '.claude/skills/pointer-init/SKILL.md',
      '.claude/skills/pointer-feedback/SKILL.md',
      '.pointer/pointer.sh',
    ]) {
      const stat = await fs.stat(join(dir, rel));
      assert.ok(stat.isFile() || stat.isSymbolicLink(), `${rel} should exist`);
    }
  } finally {
    await fs.rm(dir, { recursive: true, force: true });
    await stub.close();
  }
});

test('update re-installs a skill file that was deleted after a previous install', async () => {
  const stub = await stubServer('2026.09.16');
  const dir = await scratch({ server: stub.url, project: 'demo', environment: 'local', aiTool: 'claude-code' });
  try {
    // A first run installs everything.
    const first = await updateCommand(dir, { server: stub.url });
    assert.equal(first, 0);

    const feedbackSkill = join(dir, '.claude/skills/pointer-feedback/SKILL.md');
    await fs.access(feedbackSkill); // sanity: it exists before we delete it
    await fs.rm(feedbackSkill);

    const second = await updateCommand(dir, { server: stub.url });
    assert.equal(second, 0);

    await fs.access(feedbackSkill); // re-installed
  } finally {
    await fs.rm(dir, { recursive: true, force: true });
    await stub.close();
  }
});

test('update --check reports missing files without installing them', async () => {
  const stub = await stubServer('2026.09.16');
  const dir = await scratch({ server: stub.url, project: 'demo', environment: 'local', aiTool: 'claude-code' });
  try {
    const code = await updateCommand(dir, { server: stub.url, check: true });
    assert.equal(code, 0);
    await assert.rejects(fs.access(join(dir, '.pointer/pointer.sh')), 'check must not install anything');
  } finally {
    await fs.rm(dir, { recursive: true, force: true });
    await stub.close();
  }
});

test('update reports up to date once files are installed and match the server version', async () => {
  const stub = await stubServer('2026.09.16');
  const dir = await scratch({ server: stub.url, project: 'demo', environment: 'local', aiTool: 'claude-code' });
  try {
    await updateCommand(dir, { server: stub.url });
    const code = await updateCommand(dir, { server: stub.url });
    assert.equal(code, 0);
  } finally {
    await fs.rm(dir, { recursive: true, force: true });
    await stub.close();
  }
});

test('update removes legacy .pointer/credentials.env.example and .pointer/.token_cache, never touching credentials.env', async () => {
  const stub = await stubServer('2026.09.16');
  const dir = await scratch({ server: stub.url, project: 'demo', environment: 'local', aiTool: 'claude-code' });
  try {
    await fs.writeFile(join(dir, '.pointer/credentials.env.example'), 'POINTER_API_KEY=\n', 'utf8');
    await fs.writeFile(join(dir, '.pointer/.token_cache'), '{"token":"stale"}', 'utf8');
    await fs.writeFile(join(dir, '.pointer/credentials.env'), 'POINTER_API_KEY=ptr_good\n', 'utf8');

    await updateCommand(dir, { server: stub.url });

    await assert.rejects(fs.access(join(dir, '.pointer/credentials.env.example')));
    await assert.rejects(fs.access(join(dir, '.pointer/.token_cache')));

    const creds = await fs.readFile(join(dir, '.pointer/credentials.env'), 'utf8');
    assert.equal(creds, 'POINTER_API_KEY=ptr_good\n');
  } finally {
    await fs.rm(dir, { recursive: true, force: true });
    await stub.close();
  }
});
