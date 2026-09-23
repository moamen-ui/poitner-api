import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import { ICON } from './icons';
import { t, setLang } from './i18n';
import { timeAgo } from './dom';
import { TPL } from './templates';
import type { Comment } from './types';
import { PointerFeedback } from './element';

describe('widget improvements', () => {
  beforeEach(() => {
    setLang('en');
  });

  describe('i18n strings', () => {
    it('returns English readMore, readLess, fields.extra, fields.edit, fields.seeMore, fields.seeLess', () => {
      setLang('en');
      expect(t('card.readMore')).toBe('Read more');
      expect(t('card.readLess')).toBe('Read less');
      expect(t('fields.extra')).toBe('Extra fields');
      expect(t('fields.edit')).toBe('Extra fields');
      expect(t('fields.seeMore')).toBe('See more');
      expect(t('fields.seeLess')).toBe('See less');
    });

    it('returns Arabic readMore, readLess, fields.extra, fields.edit, fields.seeMore, fields.seeLess', () => {
      setLang('ar');
      expect(t('card.readMore')).toBe('قراءة المزيد');
      expect(t('card.readLess')).toBe('قراءة أقل');
      expect(t('fields.extra')).toBe('حقول إضافية');
      expect(t('fields.edit')).toBe('حقول إضافية');
      expect(t('fields.seeMore')).toBe('عرض المزيد');
      expect(t('fields.seeLess')).toBe('عرض أقل');
    });

    // R5-65 fix-now #4: unread/99+, Dismiss notification, Open full screenshot, Element
    // screenshot — verify both catalogs actually carry these keys (previously hardcoded English
    // baked straight into templates.ts/element.ts).
    it('resolves the RTL-audit hardcoded strings in English', () => {
      setLang('en');
      expect(t('toolbar.unreadSuffix', { count: 5 })).toBe(', 5 unread');
      expect(t('toolbar.countCap')).toBe('99+');
      expect(t('toast.dismissNotification')).toBe('Dismiss notification');
      expect(t('card.openFullScreenshot')).toBe('Open full screenshot');
      expect(t('card.elementScreenshot')).toBe('Element screenshot');
    });

    it('resolves the RTL-audit hardcoded strings in Arabic', () => {
      setLang('ar');
      expect(t('toolbar.unreadSuffix', { count: 5 })).toBe('، 5 غير مقروءة');
      expect(t('toolbar.countCap')).toBe('99+');
      expect(t('toast.dismissNotification')).toBe('إغلاق الإشعار');
      expect(t('card.openFullScreenshot')).toBe('فتح لقطة الشاشة كاملة');
      expect(t('card.elementScreenshot')).toBe('لقطة شاشة العنصر');
    });
  });

  // R5-65 fix-now #3: timeAgo() previously always returned hardcoded English ("2m ago"). Now
  // routed through Intl.RelativeTimeFormat(getLang()) so Arabic gets correct native plural forms.
  describe('timeAgo', () => {
    afterEach(() => setLang('en'));

    it('returns "" for a missing or unparseable timestamp', () => {
      expect(timeAgo(null)).toBe('');
      expect(timeAgo(undefined)).toBe('');
      expect(timeAgo('not-a-date')).toBe('');
    });

    it('formats English relative time', () => {
      setLang('en');
      expect(timeAgo(new Date(Date.now() - 5 * 60 * 1000).toISOString())).toBe('5 minutes ago');
      expect(timeAgo(new Date(Date.now() - 3 * 60 * 60 * 1000).toISOString())).toBe('3 hours ago');
    });

    it('formats Arabic relative time with correct native plural forms (2, 3-10, 11+)', () => {
      setLang('ar');
      expect(timeAgo(new Date(Date.now() - 2 * 60 * 1000).toISOString())).toBe('قبل دقيقتين');
      expect(timeAgo(new Date(Date.now() - 5 * 60 * 1000).toISOString())).toBe('قبل 5 دقائق');
      expect(timeAgo(new Date(Date.now() - 11 * 60 * 1000).toISOString())).toBe('قبل 11 دقيقة');
    });
  });

  describe('icons', () => {
    it('has extraFields icon', () => {
      expect(ICON.extraFields).toContain('<path d="M11 12H3"/>');
    });

    it('has camera icon', () => {
      expect(ICON.camera).toContain('<circle cx="12" cy="13" r="4"/>');
    });

    it('has bug icon', () => {
      expect(ICON.bug).toContain('<path d="m8 2 1.88 1.88"/>');
    });

    it('has updated restore icon', () => {
      expect(ICON.restore).toContain('viewBox="0 0 24 24"');
      expect(ICON.restore).toContain('<path d="M3 12a9 9 0 1 0 9-9 9.75 9.75 0 0 0-6.74 2.74L3 8"/>');
    });
  });

  describe('templates', () => {
    it('renders chrome with #fbk-updates visible (not fbk-hidden) for a signed-in user', () => {
      const html = TPL.chrome('User', 'Admin');
      expect(html).toContain('id="fbk-updates"');
      expect(html).not.toMatch(/class="[^"]*fbk-hidden[^"]*"[^>]*id="fbk-updates"/);
    });

    it('does not render #fbk-updates for a signed-out visitor (no displayName)', () => {
      const html = TPL.chrome('', '');
      expect(html).not.toContain('id="fbk-updates"');
    });

    it('toggles the unread dot on #fbk-notify-count with the unread count', () => {
      const withUnread = TPL.chrome('User', 'Admin', '', '', 3);
      expect(withUnread).toMatch(/id="fbk-notify-count"/);
      expect(withUnread).not.toMatch(/class="fbk-toolbar-dot fbk-hidden"[^>]*id="fbk-notify-count"/);

      const withoutUnread = TPL.chrome('User', 'Admin', '', '', 0);
      expect(withoutUnread).toMatch(/class="fbk-toolbar-dot fbk-hidden"[^>]*id="fbk-notify-count"/);
    });

    it('renders card with fbk-text-clamped and toggle-read-more button inside fbk-text', () => {
      const c: Comment = {
        id: 101,
        status: 'open',
        body: 'A long comment text that needs to be clamped',
      };
      const html = TPL.card(c, 0);
      expect(html).toContain('class="fbk-text fbk-text-clamped" data-id="101"');
      expect(html).toContain('<button type="button" class="fbk-read-more-btn fbk-hidden" data-act="toggle-read-more" data-id="101">… Read more</button>');
      expect(html).toContain('<span class="fbk-text-content">A long comment text that needs to be clamped</span>');
    });

    it('wraps custom fields in fbk-card-fields-wrapper and gates edit button by permission', () => {
      const c: Comment = {
        id: 102,
        status: 'open',
        body: 'Comment with fields',
        _mine: true,
        customFields: [
          { key: 'issue', label: 'Issue', type: 1, value: 'PROJ-123' },
        ],
      };
      const htmlMine = TPL.card(c, 0);
      expect(htmlMine).toContain('class="fbk-card-fields-wrapper"');
      expect(htmlMine).toContain('class="fbk-card-fields-edit-btn"');
      expect(htmlMine).toContain('data-act="edit-fields"');
      expect(htmlMine).toContain('data-id="102"');
      expect(htmlMine).toContain('<dd class="fbk-card-field-val"><span class="fbk-card-field-val-text">PROJ-123</span><button type="button" class="fbk-field-more-btn fbk-hidden" data-act="toggle-field-more">See more</button></dd>');

      const htmlNotMine = TPL.card({ ...c, _mine: false, _canVerify: false }, 0);
      expect(htmlNotMine).toContain('class="fbk-card-fields-wrapper"');
      expect(htmlNotMine).not.toContain('class="fbk-card-fields-edit-btn"');
    });

    it('renders cardMenu with extraFields icon and fields.extra text', () => {
      const c: Comment = { id: 103, status: 'open' };
      const html = TPL.cardMenu(c, false, true);
      expect(html).toContain('data-menu-act="edit-fields"');
      expect(html).toContain(ICON.extraFields);
      expect(html).toContain(`<span>${t('fields.extra')}</span>`);
    });

    it('renders popover with more-fields link row and toggle switch before label with fbk-toggle-switch-sm', () => {
      const meta = {
        _tag: 'button',
        _sourcePath: null,
        _snapshotPreview: '<button>Click</button>',
        selector: 'button',
        snapshot: '<button>Click</button>',
        classes: '',
        computedStyles: '',
        appliedCssRules: '',
        sourcePath: null,
        parentInfo: '',
      };
      const fields = [{ key: 'notes', label: 'Notes', type: 1, enabled: true }];
      const html = TPL.popover(meta, 10, 20, true, [], true, fields);
      
      expect(html).toContain('class="fbk-popover-more-fields-row"');
      expect(html).toContain('class="fbk-popover-more-link"');
      expect(html).toContain('id="fbk-more-fields"');
      expect(html).toContain(t('fields.extra'));

      // Switches before label with fbk-toggle-switch-sm
      expect(html).toContain('class="fbk-popover-toggle-row"');
      expect(html).toContain('<div class="fbk-popover-toggle-row"><button type="button" class="fbk-toggle-switch fbk-toggle-switch-sm" id="fbk-comment-shot"');
      expect(html).toContain('<div class="fbk-popover-toggle-row"><button type="button" class="fbk-toggle-switch fbk-toggle-switch-sm" id="fbk-comment-bug"');
      expect(html).toContain(ICON.camera);
      expect(html).toContain(ICON.bug);
    });

    it('renders card page URL with small font style classes (fbk-caption and fbk-card-page)', () => {
      const c: Comment = {
        id: 104,
        status: 'open',
        body: 'Page URL test',
        element: { pageUrl: 'https://example.com/settings/profile' },
      };
      const html = TPL.card(c, 0);
      expect(html).toContain('class="fbk-caption fbk-card-page"');
      expect(html).toContain('title="https://example.com/settings/profile"');
      expect(html).toContain('/settings/profile');
    });
  });

  describe('card styles', () => {
    it('defines small font-size token for .fbk-card-page in _card.scss', () => {
      const scss = fs.readFileSync(path.resolve(__dirname, 'styles/_card.scss'), 'utf-8');
      expect(scss).toMatch(/\.fbk-card-page\s*\{[^}]*font-size:\s*v\.token\('size-xs'\)/);
    });

    it('defines center alignment and expanded flex-start for .fbk-reply in _card.scss', () => {
      const scss = fs.readFileSync(path.resolve(__dirname, 'styles/_card.scss'), 'utf-8');
      expect(scss).toMatch(/\.fbk-reply\s*\{[^}]*align-items:\s*center/);
      expect(scss).toMatch(/&\.expanded\s*\{[^}]*align-items:\s*flex-start/);
    });
  });

  describe('location change and pin rendering', () => {
    let el: PointerFeedback;

    beforeEach(() => {
      if (!customElements.get('pointer-feedback')) {
        customElements.define('pointer-feedback', PointerFeedback);
      }
      el = document.createElement('pointer-feedback') as PointerFeedback;
      el.setAttribute('project', 'test-proj');
      document.body.appendChild(el);
    });

    afterEach(() => {
      el.remove();
      vi.restoreAllMocks();
    });

    it('triggers renderPins() when pointer:locationchange is dispatched', () => {
      const renderPinsSpy = vi.spyOn(el, 'renderPins');
      (el as any)._lastUrl = 'http://localhost/previous-url';
      window.dispatchEvent(new Event('pointer:locationchange'));
      expect(renderPinsSpy).toHaveBeenCalled();
    });

    it('triggers renderPins() when popstate is dispatched', () => {
      const renderPinsSpy = vi.spyOn(el, 'renderPins');
      (el as any)._lastUrl = 'http://localhost/previous-url';
      window.dispatchEvent(new Event('popstate'));
      expect(renderPinsSpy).toHaveBeenCalled();
    });
  });

  // R5-65 fix-now #1 (X8): the shadow root's own `dir` now follows the WIDGET's language (set on
  // the host element, same mechanism as data-fbk-theme) instead of being forced ltr always.
  describe('RTL direction', () => {
    let dirEl: PointerFeedback;

    beforeEach(() => {
      try { localStorage.removeItem('pointer_widget_language'); } catch { /* ignore */ }
      if (!customElements.get('pointer-feedback')) {
        customElements.define('pointer-feedback', PointerFeedback);
      }
      dirEl = document.createElement('pointer-feedback') as PointerFeedback;
      dirEl.setAttribute('project', 'test-proj');
    });

    afterEach(() => {
      dirEl.remove();
      try { localStorage.removeItem('pointer_widget_language'); } catch { /* ignore */ }
      setLang('en');
    });

    it('defaults to dir="ltr" for English', () => {
      document.body.appendChild(dirEl);
      expect(dirEl.getAttribute('dir')).toBe('ltr');
    });

    it('sets dir="rtl" when resolveLang() falls back to an Arabic navigator.language', () => {
      const nav = vi.spyOn(window.navigator, 'language', 'get').mockReturnValue('ar-SA');
      document.body.appendChild(dirEl);
      expect(dirEl.getAttribute('dir')).toBe('rtl');
      nav.mockRestore();
    });

    it('flips dir when the language override changes at runtime (setLanguageOverride)', () => {
      document.body.appendChild(dirEl);
      expect(dirEl.getAttribute('dir')).toBe('ltr');
      (dirEl as any).setLanguageOverride('ar');
      expect(dirEl.getAttribute('dir')).toBe('rtl');
      (dirEl as any).setLanguageOverride('en');
      expect(dirEl.getAttribute('dir')).toBe('ltr');
    });

    // R5-65 follow-up: older Safari (<14) has no Intl.RelativeTimeFormat, and CONSTRUCTING it
    // there throws — unguarded, timeAgo would kill renderPins() for the whole page. Verify the
    // feature guard falls back to the plain localized date string instead of throwing.
    it('timeAgo returns a non-throwing date string when Intl.RelativeTimeFormat is undefined', () => {
      const desc = Object.getOwnPropertyDescriptor(Intl, 'RelativeTimeFormat');
      expect(desc).toBeDefined();
      Object.defineProperty(Intl, 'RelativeTimeFormat', { value: undefined, configurable: true });
      try {
        const iso = new Date(Date.now() - 5 * 60 * 1000).toISOString();
        const out = timeAgo(iso);
        expect(typeof out).toBe('string');
        expect(out).toBe(new Date(iso).toLocaleDateString());
      } finally {
        Object.defineProperty(Intl, 'RelativeTimeFormat', desc!);
      }
    });

    // R5-65 follow-up: the boot-time invite-redemption failure toast was the last hard-coded
    // English user-visible string in element.ts — pin the key in BOTH catalogs.
    it('resolves toast.inviteLinkInvalid in English and Arabic', () => {
      setLang('en');
      expect(t('toast.inviteLinkInvalid')).toBe('This invite link is invalid or expired — ask for a new one.');
      setLang('ar');
      expect(t('toast.inviteLinkInvalid')).toBe('رابط الدعوة هذا غير صالح أو منتهي الصلاحية — اطلب رابطًا جديدًا.');
    });
  });
});
