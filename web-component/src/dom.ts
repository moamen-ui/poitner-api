import { HL_CLASS } from './constants';
import type { Comment } from './types';
import { getLang } from './i18n';

export const escapeHtml = (s: unknown): string =>
  String(s == null ? '' : s)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');

// First 1-2 letters for the toolbar account avatar (e.g. "Sara Ali" → "SA", "sara" → "SA").
// Falls back to '?' for an empty/whitespace-only name so the avatar never renders blank.
export const initials = (name: string): string => {
  const parts = (name || '').trim().split(/\s+/).filter(Boolean);
  if (parts.length === 0) return '?';
  if (parts.length === 1) return parts[0]!.slice(0, 2).toUpperCase();
  return (parts[0]![0] + parts[parts.length - 1]![0]).toUpperCase();
};

// Localized via the platform's own Intl.RelativeTimeFormat rather than new i18n.ts catalog keys —
// Arabic's plural rules for "minute(s)/hour(s)/day(s)" need several forms (1, 2, 3-10, 11+ — the
// exact gap the RTL audit flagged for i18n.ts's own hand-rolled `pin.reply`/`pin.replies` pair)
// that Intl already gets right natively, at zero bundle cost, instead of hand-rolling
// `_one/_few/_many` keys x2 languages here too. `numeric: 'auto'` additionally picks up correct
// idioms where CLDR has them (verified: en "now"/"yesterday", ar "الآن"/"أمس"), falling back to a
// plain count otherwise. Rebuilt per call rather than cached per language: this only ever renders
// one pin tooltip at a time, so the extra allocation isn't worth the code to memoize it.
// Feature-guarded, not assumed: older Safari (<14) lacks Intl.RelativeTimeFormat and CONSTRUCTING
// it there throws — inside timeAgo that would kill renderPins() for the whole page, not just one
// tooltip — so when it's missing every branch falls back to the plain localized date below.
export const timeAgo = (iso?: string | null): string => {
  if (!iso) return '';
  const then = new Date(iso).getTime();
  if (Number.isNaN(then)) return '';
  const seconds = Math.round((Date.now() - then) / 1000);
  const rtf = typeof Intl.RelativeTimeFormat === 'function'
    ? new Intl.RelativeTimeFormat(getLang(), { numeric: 'auto' })
    : null;
  if (!rtf) return new Date(iso).toLocaleDateString();
  if (seconds < 45) return rtf.format(0, 'second');
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return rtf.format(-minutes, 'minute');
  const hours = Math.round(minutes / 60);
  if (hours < 24) return rtf.format(-hours, 'hour');
  const days = Math.round(hours / 24);
  if (days < 30) return rtf.format(-days, 'day');
  return new Date(iso).toLocaleDateString();
};

// Builds a CSS clip-path (SVG path, evenodd fill rule) covering `refBox` with a rectangular hole
// cut out for each rect — used to keep our own UI clickable/visible through a host app's modal
// backdrop or dialog content pane (see Element.punchBackdropHoles) without disabling it elsewhere.
// clip-path coordinates are relative to the CLIPPED ELEMENT's OWN border box, not the viewport —
// `refBox` must be that element's own getBoundingClientRect() so hole rects (which are viewport-
// relative, from getBoundingClientRect() elsewhere on the page) get translated correctly. For a
// full-viewport backdrop this reduces to the same thing (refBox.left/top ≈ 0); for a small,
// positioned dialog pane it doesn't — using viewport coordinates directly there punches the hole at
// a meaningless offset inside the pane's own box, which was confirmed to silently no-op.
// A 2px outward pad on each hole avoids a 1px sliver the backdrop could still catch at the edge.
export const buildClipPathWithHoles = (
  rects: DOMRect[],
  refBox: { left: number; top: number; width: number; height: number },
): string => {
  const w = refBox.width, h = refBox.height;
  let d = `M0 0H${w}V${h}H0Z`;
  for (const r of rects) {
    const x1 = Math.max(0, r.left - refBox.left - 2), y1 = Math.max(0, r.top - refBox.top - 2);
    const x2 = Math.min(w, r.right - refBox.left + 2), y2 = Math.min(h, r.bottom - refBox.top + 2);
    if (x2 <= x1 || y2 <= y1) continue;
    d += ` M${x1} ${y1}H${x2}V${y2}H${x1}Z`;
  }
  return `path(evenodd, "${d}")`;
};

// One global style for the host-page hover highlight (lives in light DOM by
// necessity — it decorates the host app's own elements, not our shadow UI).
export const ensureHighlightStyle = (): void => {
  if (document.getElementById('pointer-feedback-hl-style')) return;

  // A dashed outline (not `border`) so it never changes the host element's own box size or
  // layout — same reason it's `outline-color`/box-shadow that animate below, not anything that
  // would. The color cycles blue → violet → blue (our primary and accent tokens, as literals:
  // this stylesheet lives in the HOST page's light DOM, so it can't reach the shadow-scoped
  // --fbk-* custom properties) with a matching glow, echoing the same 2.4s pulse timing already
  // used for the launcher and pin attention rings, so the whole widget's motion language matches.
  const css = `
.${HL_CLASS}{
  outline:2px dashed #0969da!important;
  outline-offset:1px!important;
  cursor:crosshair!important;
  box-shadow:0 0 0 0 rgba(9,105,218,.3)!important;
  animation:pointer-feedback-hl-pulse 2.4s cubic-bezier(.25,1,.5,1) infinite!important;
}
@keyframes pointer-feedback-hl-pulse{
  0%,100%{outline-color:#0969da;box-shadow:0 0 0 0 rgba(9,105,218,.3);}
  50%{outline-color:#7c3aed;box-shadow:0 0 10px 2px rgba(124,58,237,.3);}
}
@media (prefers-reduced-motion: reduce){
  .${HL_CLASS}{animation:none!important;outline-color:#0969da!important;box-shadow:none!important;}
}`;

  // A constructed stylesheet first. A <style> element is markup, so `style-src` blocks it on a
  // strict-CSP host and the pick highlight silently never appears — on exactly the sites most
  // likely to run one. adoptedStyleSheets is CSSOM, which the directive does not govern, so it
  // works with no nonce and nothing for the host page to configure.
  try {
    if ('adoptedStyleSheets' in Document.prototype && typeof CSSStyleSheet !== 'undefined') {
      const sheet = new CSSStyleSheet();
      sheet.replaceSync(css);
      document.adoptedStyleSheets = [...document.adoptedStyleSheets, sheet];
      // A marker so the guard above still short-circuits; it carries no styles itself.
      const marker = document.createElement('meta');
      marker.id = 'pointer-feedback-hl-style';
      document.head.appendChild(marker);
      return;
    }
  } catch {
    // Fall through to the <style> element below.
  }

  const s = document.createElement('style');
  s.id = 'pointer-feedback-hl-style';
  s.textContent = css;
  document.head.appendChild(s);
};

// --- Element selector (ported from the original inject.js) ----------------
export const generateSelector = (el: Element): string => {
  if (el === document.documentElement) return 'html';
  if (el === document.body) return 'body';

  if (el.id) {
    try {
      if (document.querySelector('#' + CSS.escape(el.id)) === el) return '#' + el.id;
    } catch (e) {}
  }

  const parts: string[] = [];
  let cur: Element | null = el;
  while (cur && cur !== document.body && cur !== document.documentElement) {
    let selector = cur.tagName.toLowerCase();
    if (cur.id) {
      selector += '#' + cur.id;
      parts.unshift(selector);
      cur = null;
      break;
    }
    let nth = 1;
    let sib = cur.previousElementSibling;
    while (sib) {
      if (sib.tagName.toLowerCase() === cur.tagName.toLowerCase()) nth++;
      sib = sib.previousElementSibling;
    }
    if (nth > 1) selector += `:nth-of-type(${nth})`;
    parts.unshift(selector);
    cur = cur.parentElement;
  }
  if (cur === document.body) parts.unshift('body');
  else if (cur === document.documentElement) parts.unshift('html');
  return parts.join(' > ');
};

// True when `comment` was captured on the page currently loaded — compared by pathname only, so
// a query-string or hash difference (a filter param, a scroll anchor) doesn't count as "another
// page". A comment with no recorded `element.pageUrl` (created before that field existed) is
// treated as current, since there's nothing to compare against. Used to keep a comment's pin off
// pages it doesn't belong to (see renderPins) — a selector match alone isn't enough, since two
// different pages can easily share the same DOM structure (a repeated layout/component).
export const isCurrentPage = (comment: Comment): boolean => {
  const url = comment.element && comment.element.pageUrl;
  if (!url) return true;
  try {
    return new URL(url, window.location.href).pathname === window.location.pathname;
  } catch (e) {
    return true;
  }
};

// Re-find an element from a stored comment (selector first, snapshot fallback).
export const matchElement = (comment: Comment): Element | null => {
  const selector = comment.element && comment.element.selector;
  const snapshot = comment.element && comment.element.snapshot;
  if (selector) {
    try {
      const el = document.querySelector(selector);
      if (el) return el;
    } catch (e) {}
  }
  if (snapshot) {
    const all = document.querySelectorAll('*');
    for (const el of Array.from(all)) {
      if (el.outerHTML === snapshot) return el;
    }
  }
  return null;
};

// True when the host page renders right-to-left. Read live (not cached) so the
// launcher corner tracks a page that toggles direction at runtime.
export const pageIsRtl = (): boolean => {
  try {
    const html = document.documentElement;
    const attr = (html.getAttribute('dir') || document.body?.getAttribute('dir') || '').toLowerCase();
    if (attr === 'rtl' || attr === 'ltr') return attr === 'rtl';
    return getComputedStyle(html).direction === 'rtl';
  } catch (e) {
    return false;
  }
};

/**
 * Applies the position an element carries in `data-fbk-left` / `data-fbk-top`.
 *
 * These two are computed per element — a popover anchored to a click, a pin anchored to the thing
 * it marks — so they cannot become a CSS class. They also cannot stay in a `style` attribute:
 * markup parsed by innerHTML under a strict Content-Security-Policy has its inline styles dropped,
 * which on a nonce-CSP host leaves every pin stacked at the origin.
 *
 * Assigning through the CSSOM is the way out. `style-src` governs style attributes and <style>
 * elements in markup; setting a property on an element's style object is not affected, so this
 * works under the strictest policy and needs no nonce.
 */
export function applyDataPosition(root: ParentNode, selector: string): void {
  root.querySelectorAll<HTMLElement>(selector).forEach((el) => {
    const left = el.dataset.fbkLeft;
    const top = el.dataset.fbkTop;
    if (left !== undefined) el.style.left = `${left}px`;
    if (top !== undefined) el.style.top = `${top}px`;
  });
}
