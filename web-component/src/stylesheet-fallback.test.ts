import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { PointerFeedback } from './element';

describe('stylesheet fallback', () => {
  let originalFetch: typeof globalThis.fetch;
  let originalCSSStyleSheet: any;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
    originalCSSStyleSheet = (globalThis as any).CSSStyleSheet;

    class MockCSSStyleSheet {
      cssRules: any[] = [];
      replace = vi.fn().mockImplementation(async (_text: string) => { return; });
      replaceSync = vi.fn();
    }
    (globalThis as any).CSSStyleSheet = MockCSSStyleSheet;

    if (!('adoptedStyleSheets' in ShadowRoot.prototype)) {
      Object.defineProperty(ShadowRoot.prototype, 'adoptedStyleSheets', {
        configurable: true,
        writable: true,
        value: [],
      });
    }

    if (!customElements.get('pointer-feedback')) {
      customElements.define('pointer-feedback', PointerFeedback);
    }
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    (globalThis as any).CSSStyleSheet = originalCSSStyleSheet;
    document.body.innerHTML = '';
    vi.restoreAllMocks();
  });

  it('link error -> fetch called with integrity and adoptedStyleSheets set on shadow root', async () => {
    const fetchMock = vi.fn().mockImplementation(async (url: string) => {
      if (String(url).includes('widget-status')) {
        return { ok: true, json: async () => ({ active: true }) };
      }
      return {
        ok: true,
        text: async () => '.pf-root { display: block; }',
      };
    });
    globalThis.fetch = fetchMock;

    const el = document.createElement('pointer-feedback') as PointerFeedback;
    el.setAttribute('project', 'test-p');
    document.body.appendChild(el);

    const link = el.shadowRoot!.querySelector('link') as HTMLLinkElement;
    expect(link).toBeTruthy();

    link.dispatchEvent(new Event('error'));

    await (el as any)._stylesReady();

    const cssCalls = fetchMock.mock.calls.filter(([url]) => String(url).includes('pointer.css'));
    expect(cssCalls.length).toBeGreaterThan(0);
    const [calledUrl, fetchOpts] = cssCalls[0];
    expect(calledUrl).toContain('pointer.css');
    expect(fetchOpts.mode).toBe('cors');
    expect(el.shadowRoot!.adoptedStyleSheets.length).toBeGreaterThan(0);
  });

  it('link load -> no fetch', async () => {
    const fetchMock = vi.fn().mockImplementation(async (url: string) => {
      if (String(url).includes('widget-status')) {
        return { ok: true, json: async () => ({ active: true }) };
      }
      return { ok: true, text: async () => '' };
    });
    globalThis.fetch = fetchMock;

    const el = document.createElement('pointer-feedback') as PointerFeedback;
    el.setAttribute('project', 'test-p');
    document.body.appendChild(el);

    const link = el.shadowRoot!.querySelector('link') as HTMLLinkElement;
    expect(link).toBeTruthy();

    link.dispatchEvent(new Event('load'));

    await (el as any)._stylesReady();

    const cssCalls = fetchMock.mock.calls.filter(([url]) => String(url).includes('pointer.css'));
    expect(cssCalls.length).toBe(0);
    expect(el.shadowRoot!.adoptedStyleSheets.length).toBe(0);
  });

  it('timer expiry without error -> no fetch (stubbed CSSStyleSheet)', async () => {
    vi.useFakeTimers();
    try {
      const fetchMock = vi.fn().mockImplementation(async (url: string) => {
        if (String(url).includes('widget-status')) {
          return { ok: true, json: async () => ({ active: true }) };
        }
        return { ok: true, text: async () => '' };
      });
      globalThis.fetch = fetchMock;

      const el = document.createElement('pointer-feedback') as PointerFeedback;
      el.setAttribute('project', 'test-p');
      document.body.appendChild(el);

      const stylesReadyPromise = (el as any)._stylesReady();
      vi.advanceTimersByTime(1600);

      await stylesReadyPromise;

      const cssCalls = fetchMock.mock.calls.filter(([url]) => String(url).includes('pointer.css'));
      expect(cssCalls.length).toBe(0);
      expect(el.shadowRoot!.adoptedStyleSheets.length).toBe(0);
    } finally {
      vi.useRealTimers();
    }
  });
});
