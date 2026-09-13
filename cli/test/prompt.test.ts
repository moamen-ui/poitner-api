// The interactive prompt layer.
//
// These drive the menus with synthetic keypress events rather than a pseudo-terminal: a pty test
// depends on how fast the child starts and how the OS buffers writes, which makes it flaky and
// makes a genuine failure indistinguishable from a slow machine. What matters here is the key
// handling, and that is deterministic.
import { test, before, after } from 'node:test';
import * as assert from 'node:assert';
import { select, multiSelect, closePrompts } from '../src/prompt.js';

let realIsTTY: unknown;
let realSetRawMode: unknown;

before(() => {
  realIsTTY = (process.stdin as any).isTTY;
  realSetRawMode = (process.stdin as any).setRawMode;
  // The prompts refuse to run without a TTY (by design — see assertInteractive), and the test
  // runner gives them a pipe.
  (process.stdin as any).isTTY = true;
  (process.stdin as any).setRawMode = () => process.stdin;
});

after(() => {
  (process.stdin as any).isTTY = realIsTTY;
  (process.stdin as any).setRawMode = realSetRawMode;
  closePrompts();
});

const key = (name: string) => process.stdin.emit('keypress', '', { name });
const settle = () => new Promise((r) => setTimeout(r, 20));

test('select: arrow keys move the cursor and enter takes the highlighted row', async () => {
  const pending = select('Pick one', ['local', 'staging', 'production'], 'local');
  await settle();
  key('down');
  key('down');
  key('return');
  assert.strictEqual(await pending, 'production');
});

test('select: the default starts under the cursor, not the first row', async () => {
  const pending = select('Pick one', ['local', 'staging', 'production'], 'staging');
  await settle();
  key('return');
  assert.strictEqual(await pending, 'staging');
});

test('select: the cursor wraps around both ends', async () => {
  const pending = select('Pick one', ['a', 'b', 'c'], 'a');
  await settle();
  key('up'); // wraps to the last row
  key('return');
  assert.strictEqual(await pending, 'c');
});

test('multiSelect: "a" ticks everything', async () => {
  const pending = multiSelect('Tools', ['claude-code', 'cursor', 'opencode'], []);
  await settle();
  key('a');
  key('return');
  assert.deepStrictEqual(await pending, ['claude-code', 'cursor', 'opencode']);
});

test('multiSelect: "a" a second time clears the selection again', async () => {
  const pending = multiSelect('Tools', ['claude-code', 'cursor', 'opencode'], []);
  await settle();
  key('a');
  key('a');
  // Nothing ticked now, so enter falls back to the row under the cursor rather than returning [].
  key('return');
  assert.deepStrictEqual(await pending, ['claude-code']);
});

test('multiSelect: space toggles the row under the cursor', async () => {
  const pending = multiSelect('Tools', ['claude-code', 'cursor', 'opencode'], []);
  await settle();
  key('down');
  key('space');
  key('return');
  assert.deepStrictEqual(await pending, ['cursor']);
});

test('multiSelect: enter with nothing ticked never returns an empty answer', async () => {
  const pending = multiSelect('Tools', ['claude-code', 'cursor', 'opencode'], []);
  await settle();
  key('return');
  const chosen = await pending;
  assert.ok(chosen.length > 0, 'an empty selection would leave init with no tool to install');
  assert.deepStrictEqual(chosen, ['claude-code']);
});

test('multiSelect: defaults arrive pre-ticked', async () => {
  const pending = multiSelect('Tools', ['claude-code', 'cursor', 'opencode'], ['cursor']);
  await settle();
  key('return');
  assert.deepStrictEqual(await pending, ['cursor']);
});
