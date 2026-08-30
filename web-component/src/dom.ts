import { HL_CLASS } from './constants';
import type { Comment } from './types';

export const escapeHtml = (s: unknown): string =>
  String(s == null ? '' : s)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');

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
  const s = document.createElement('style');
  s.id = 'pointer-feedback-hl-style';
  s.textContent = `.${HL_CLASS}{outline:2px dashed #2563eb!important;outline-offset:1px!important;cursor:crosshair!important;}`;
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
