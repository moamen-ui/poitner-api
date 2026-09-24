// DB-18: workspace paused / scheduled-for-deletion behaviour in the widget.
// A write hitting 423 is handled once, centrally, in api() (see element.ts's `api()` and
// `_handlePaused()`): it flips `_paused`, re-renders the chrome/sidebar read-only, and shows the
// `paused.notice` toast exactly once per page — every mutation call site then bails out on
// `r.status === 423` before it would otherwise show its own generic "failed" toast.
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { setLang, t } from './i18n';
import { PointerFeedback } from './element';

function jsonResponse(status: number, body: unknown = {}, headers: Record<string, string> = {}): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json', ...headers },
  });
}

describe('DB-18: workspace paused/frozen (widget)', () => {
  let el: PointerFeedback;

  beforeEach(() => {
    setLang('en');
    if (!customElements.get('pointer-feedback')) {
      customElements.define('pointer-feedback', PointerFeedback);
    }
    el = document.createElement('pointer-feedback') as PointerFeedback;
    el.setAttribute('project', 'test-proj');
    document.body.appendChild(el);
    // Render the real chrome (toolbar + sidebar shell) synchronously, bypassing the async
    // network-gated _boot() — same low-level poking the existing pin/RTL tests use.
    (el as any)._collapsed = false;
    (el as any).renderChrome();
  });

  afterEach(() => {
    el.remove();
    delete (window as any).__POINTER_FETCH__;
    vi.restoreAllMocks();
  });

  it('api(): a 423 sets the paused flag, toggles the read-only class, and toasts once', async () => {
    (window as any).__POINTER_FETCH__ = vi
      .fn()
      .mockResolvedValue(jsonResponse(423, { message: 'paused' }, { 'x-workspace-paused': 'true' }));
    const toastSpy = vi.spyOn(el, 'toast');

    const r1 = await (el as any).api('/api/comments/1', { method: 'PATCH', body: '{}' });
    expect(r1.status).toBe(423);
    expect((el as any)._paused).toBe(true);
    expect((el as any).root.classList.contains('fbk-paused')).toBe(true);
    expect(toastSpy).toHaveBeenCalledTimes(1);
    // DB-18 code review (Opus NIT): informational, not an error — pausing is an expected, reversible
    // admin action.
    expect(toastSpy).toHaveBeenCalledWith(t('paused.notice'));

    // A second 423 elsewhere on the page must NOT toast again ("once per page").
    const r2 = await (el as any).api('/api/comments/2', { method: 'DELETE' });
    expect(r2.status).toBe(423);
    expect(toastSpy).toHaveBeenCalledTimes(1);
  });

  it('renders the one-line paused notice in the sidebar head once paused', () => {
    (el as any)._paused = true;
    (el as any).renderChrome();
    const notice = (el as any).root.querySelector('#fbk-paused-notice');
    expect(notice.classList.contains('fbk-hidden')).toBe(false);
    expect(notice.textContent).toBe(t('paused.notice'));
  });

  it('leaves the notice hidden and the class absent while not paused', () => {
    const notice = (el as any).root.querySelector('#fbk-paused-notice');
    expect(notice.classList.contains('fbk-hidden')).toBe(true);
    expect((el as any).root.classList.contains('fbk-paused')).toBe(false);
  });

  // One representative call site per HTTP verb — the same `if (r.status === 423) return …;` guard
  // is applied identically to every other comment/reply mutation (add/edit/delete/verify/fields).
  it.each([
    ['deleteComment', () => el.deleteComment('123')],
    ['addReply', () => el.addReply('123', 'hello')],
    ['setVisibility', () => el.setVisibility({ id: '123' } as any, true)],
  ])('%s bails out on 423 without a second generic-failure toast', async (_name, run) => {
    (window as any).__POINTER_FETCH__ = vi.fn().mockResolvedValue(jsonResponse(423, { message: 'paused' }));
    const toastSpy = vi.spyOn(el, 'toast');
    await run();
    // Only the one paused toast from api() — never the method's own "failed"/"update failed" toast.
    expect(toastSpy).toHaveBeenCalledTimes(1);
    expect(toastSpy).toHaveBeenCalledWith(t('paused.notice'));
  });

  it('_checkWidgetActive() reads the paused flag from the widget-status response', async () => {
    (window as any).__POINTER_FETCH__ = vi
      .fn()
      .mockResolvedValue(jsonResponse(200, { data: { active: true, paused: true } }));
    const active = await (el as any)._checkWidgetActive();
    expect(active).toBe(true);
    expect((el as any)._paused).toBe(true);
  });

  it('uploadToServer() skips the network call outright once already paused', async () => {
    (el as any)._paused = true;
    const fetchSpy = vi.fn();
    (window as any).__POINTER_FETCH__ = fetchSpy;
    const url = await el.uploadToServer(new Blob(['x']));
    expect(url).toBeNull();
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it('uploadToServer() treats a 423 mid-upload the same as api() (flag + single toast)', async () => {
    (window as any).__POINTER_FETCH__ = vi.fn().mockResolvedValue(jsonResponse(423, { message: 'paused' }));
    const toastSpy = vi.spyOn(el, 'toast');
    const url = await el.uploadToServer(new Blob(['x']));
    expect(url).toBeNull();
    expect((el as any)._paused).toBe(true);
    expect(toastSpy).toHaveBeenCalledTimes(1);
  });

  // DB-18 code review (Opus LOW): the add-comment keyboard shortcut calls startPicking() directly,
  // bypassing the toolbar button render*() already hides while paused.
  it('startPicking() is a no-op while paused', () => {
    (el as any)._paused = true;
    el.startPicking();
    expect((el as any).picking).toBe(false);
  });

  it('startPicking() still works normally while not paused', () => {
    el.startPicking();
    expect((el as any).picking).toBe(true);
  });
});
