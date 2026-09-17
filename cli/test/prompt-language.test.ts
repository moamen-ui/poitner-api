import { test } from 'node:test';
import assert from 'node:assert/strict';
import { buildApplyPrompt } from '../src/apply/prompt.js';
import type { ApplyProjectContext, QueueItem } from '../src/apply/types.js';

const context: ApplyProjectContext = {
  productName: 'Pointer',
  projectName: 'My App',
  projectKey: 'my-app',
  commitStyle: 'Separate',
  stack: {},
};

function item(overrides: Partial<QueueItem>): QueueItem {
  return {
    id: 1,
    status: 2,
    environment: 1,
    body: 'Fix this',
    authorName: 'Sam',
    createdAt: '2026-01-01T00:00:00Z',
    element: {},
    replies: [],
    pickedActions: [],
    aiRules: [],
    isBugReport: false,
    ...overrides,
  };
}

test('buildApplyPrompt prints Language: <tag> under the item heading, outside the fence', () => {
  const prompt = buildApplyPrompt([item({ language: 'ar' })], context);
  const headingIdx = prompt.indexOf('### #1 —');
  const languageIdx = prompt.indexOf('Language: ar');
  const fenceIdx = prompt.indexOf('UNTRUSTED DATA');
  assert.ok(headingIdx >= 0 && languageIdx > headingIdx && languageIdx < fenceIdx);
});

test('buildApplyPrompt prints the unknown hint when language is absent', () => {
  const prompt = buildApplyPrompt([item({})], context);
  assert.ok(prompt.includes('Language: unknown — detect it, see translate.md'));
});

test('buildApplyPrompt prints the unknown hint when language is null', () => {
  const prompt = buildApplyPrompt([item({ language: null })], context);
  assert.ok(prompt.includes('Language: unknown — detect it, see translate.md'));
});

test('buildApplyPrompt echoes en without a translation hint', () => {
  const prompt = buildApplyPrompt([item({ language: 'en' })], context);
  assert.ok(prompt.includes('Language: en'));
  assert.ok(!prompt.includes('Language: en —'));
});
