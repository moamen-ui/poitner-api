import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import { ICON } from './icons';
import { t, setLang } from './i18n';
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
    it('renders chrome with fbk-hidden on #fbk-updates', () => {
      const html = TPL.chrome('User', 'Admin');
      expect(html).toContain('id="fbk-updates"');
      expect(html).toMatch(/class="[^"]*fbk-toolbar-btn--icon[^"]*fbk-hidden[^"]*"[^>]*id="fbk-updates"/);
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
});
