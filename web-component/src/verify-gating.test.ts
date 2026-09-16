import { describe, it, expect } from 'vitest';
import { TPL } from './templates';
import type { Comment } from './types';

const base = {
  id: 7, body: 'Fix the button', status: 'applied', authorId: 'u-1', authorName: 'A',
  replies: [], element: {}, createdAt: new Date().toISOString(),
} as unknown as Comment;

describe('verify buttons gating', () => {
  it('shows the verify buttons when the viewer may verify (author or admin) and the comment is applied + unverified', () => {
    const html = TPL.card({ ...base, _mine: false, _canVerify: true }, 0, false);
    expect(html).toContain('data-act="verify-ok"');
    expect(html).toContain('data-act="verify-reject"');
  });
  it('hides them for everyone else, once verified, and when not applied', () => {
    expect(TPL.card({ ...base, _mine: false, _canVerify: false }, 0, false)).not.toContain('verify-ok');
    expect(TPL.card({ ...base, _canVerify: true, verifiedAt: new Date().toISOString() } as Comment, 0, false)).not.toContain('verify-ok');
    expect(TPL.card({ ...base, status: 'open', _canVerify: true } as Comment, 0, false)).not.toContain('verify-ok');
  });
});
