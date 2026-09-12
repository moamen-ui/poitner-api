import { SNAPDOM_URL, SHOT_MAX_WIDTH, SHOT_HIGHLIGHT } from './constants';
import { generateSelector } from './dom';
import { detectFrameworkSourcePath } from './framework-source';
import type { Meta } from './types';

// We vendor snapdom (https://github.com/zumerlab/snapdom, MIT) under
// vendor/snapdom.js and self-host it — never a CDN, so it works offline and
// under strict host-app CSPs. The UMD build assigns `window.snapdom`. It is
// injected ONLY when a capture actually happens: a normal page load never pulls
// it in. snapdom is a single ~123KB no-dependency file and is markedly
// faster/smaller than html2canvas while reconstructing the DOM with high fidelity.
interface Snapdom {
  toCanvas(node: Element, opts?: Record<string, unknown>): Promise<HTMLCanvasElement>;
}
declare global {
  interface Window {
    snapdom?: Snapdom;
  }
}

let snapdomPromise: Promise<Snapdom> | null = null;

// Lazily inject the vendored snapdom UMD build exactly once.
function loadSnapdom(): Promise<Snapdom> {
  if (window.snapdom) return Promise.resolve(window.snapdom);
  if (snapdomPromise) return snapdomPromise;
  snapdomPromise = new Promise<Snapdom>((resolve, reject) => {
    const s = document.createElement('script');
    // A host (extension) may bundle snapdom and pass its URL; else use the script's own origin.
    s.src = window.__POINTER_CONFIG__?.snapdomUrl || SNAPDOM_URL;
    s.async = true;
    s.onload = () =>
      window.snapdom
        ? resolve(window.snapdom)
        : reject(new Error('snapdom loaded but window.snapdom missing'));
    s.onerror = () => reject(new Error('failed to load ' + SNAPDOM_URL));
    document.head.appendChild(s);
  });
  return snapdomPromise;
}

// Export a canvas to a WebP Blob, falling back to JPEG where unsupported.
function canvasToBlob(canvas: HTMLCanvasElement): Promise<Blob | null> {
  return new Promise((resolve) => {
    const done = (b: Blob | null) => resolve(b || null);
    try {
      canvas.toBlob((b) => {
        if (b) return done(b);
        // WebP unsupported (older Safari) → JPEG fallback.
        canvas.toBlob(done, 'image/jpeg', 0.6);
      }, 'image/webp', 0.6);
    } catch (e) {
      try {
        canvas.toBlob(done, 'image/jpeg', 0.6);
      } catch (e2) {
        done(null);
      }
    }
  });
}

// Capture the current viewport, draw a highlight over the picked element,
// downscale to <= SHOT_MAX_WIDTH and export a WebP (JPEG fallback) Blob.
// Resolves null on any failure — capture must never block commenting.
export async function captureScreenshot(el: Element): Promise<Blob | null> {
  try {
    const snapdom = await loadSnapdom();
    // snapdom reconstructs the DOM into an image; capturing <body> gives us the
    // rendered page. We then crop to the current viewport.
    const full = await snapdom.toCanvas(document.body, {
      backgroundColor: '#fff',
      // Skip our own shadow host entirely as a belt-and-braces measure.
      exclude: ['pointer-feedback'],
      fast: true,
    });

    const dpr = window.devicePixelRatio || 1;
    // Map viewport (CSS px) → full-page canvas pixels. snapdom rasterizes at dpr,
    // and the body canvas origin aligns with the document origin, so the scroll
    // offset (×dpr) is where the viewport begins.
    const sx = Math.max(0, Math.round(window.scrollX * dpr));
    const sy = Math.max(0, Math.round(window.scrollY * dpr));
    const vw = Math.round(window.innerWidth * dpr);
    const vh = Math.round(window.innerHeight * dpr);
    const cw = Math.min(vw, full.width - sx);
    const ch = Math.min(vh, full.height - sy);

    // Crop to viewport.
    const view = document.createElement('canvas');
    view.width = Math.max(1, cw);
    view.height = Math.max(1, ch);
    const vctx = view.getContext('2d')!;
    vctx.drawImage(full, sx, sy, view.width, view.height, 0, 0, view.width, view.height);

    // Highlight the picked element at its viewport-relative rect.
    const rect = el.getBoundingClientRect();
    const lineW = Math.max(2, Math.round(2 * dpr));
    vctx.strokeStyle = SHOT_HIGHLIGHT;
    vctx.lineWidth = lineW;
    vctx.strokeRect(
      Math.round(rect.left * dpr) + lineW / 2,
      Math.round(rect.top * dpr) + lineW / 2,
      Math.max(0, Math.round(rect.width * dpr) - lineW),
      Math.max(0, Math.round(rect.height * dpr) - lineW),
    );

    // Downscale so max width ~SHOT_MAX_WIDTH.
    let out = view;
    if (view.width > SHOT_MAX_WIDTH) {
      const scale = SHOT_MAX_WIDTH / view.width;
      const small = document.createElement('canvas');
      small.width = SHOT_MAX_WIDTH;
      small.height = Math.max(1, Math.round(view.height * scale));
      small.getContext('2d')!.drawImage(view, 0, 0, small.width, small.height);
      out = small;
    }

    return await canvasToBlob(out);
  } catch (err) {
    console.warn('[pointer-feedback] screenshot capture failed', err);
    return null;
  }
}

// HTML void elements — no closing tag, no text content.
const VOID_ELEMENTS = /^(area|base|br|col|embed|hr|img|input|link|meta|param|source|track|wbr)$/;

// A shallow, token-cheap snapshot: the element's OWN opening tag (its attributes —
// id / data-* / type / href / aria-*, which are the strongest source anchors for
// routed & generated UIs) plus its trimmed text, WITHOUT the child subtree. The full
// outerHTML (capped at 2000) drowned leaf comments in child markup / inline SVG and
// duplicated the class list; here `class` and `style` are omitted because they travel
// in the dedicated `classes` / `computedStyles` fields.
export function escapeAttr(val: string): string {
  return val.replace(/"/g, '&quot;').replace(/</g, '&lt;');
}

export function isMasked(el: Element): boolean {
  if (typeof el.closest === 'function') {
    return !!el.closest('[data-snapshot-mask]');
  }
  let curr: Element | null = el;
  while (curr) {
    if (curr.hasAttribute && curr.hasAttribute('data-snapshot-mask')) return true;
    curr = curr.parentElement;
  }
  return false;
}

export function isFormValueTag(tag: string): boolean {
  return /^(input|textarea|select|option)$/i.test(tag);
}

export function isSensitiveAttr(name: string): boolean {
  const lower = name.toLowerCase();
  if (
    lower === 'value' ||
    lower === 'authorization' ||
    lower === 'srcdoc'
  ) {
    return true;
  }
  if (lower.startsWith('data-')) {
    const rest = lower.slice(5);
    if (
      rest === 'value' ||
      rest === 'email' ||
      rest === 'token' ||
      rest === 'secret' ||
      rest.startsWith('user')
    ) {
      return true;
    }
  }
  return false;
}

export function maskAttrValue(name: string, val: string): string {
  const lower = name.toLowerCase();
  const isStructural =
    lower === 'id' ||
    lower === 'type' ||
    lower === 'role' ||
    lower.startsWith('aria-');
  if (isStructural) {
    return escapeAttr(val);
  }
  return '•••';
}

// A shallow, token-cheap snapshot: the element's OWN opening tag (its attributes —
// id / data-* / type / href / aria-*, which are the strongest source anchors for
// routed & generated UIs) plus its trimmed text, WITHOUT the child subtree. The full
// outerHTML (capped at 2000) drowned leaf comments in child markup / inline SVG and
// duplicated the class list; here `class` and `style` are omitted because they travel
// in the dedicated `classes` / `computedStyles` fields.
export function shallowSnapshot(el: Element, captureText: boolean = true): string {
  const tag = el.tagName.toLowerCase();
  const masked = isMasked(el);
  const isForm = isFormValueTag(tag);

  const rawAttrs = Array.from(el.attributes).filter(
    (a) => a.name !== 'class' && a.name !== 'style',
  );

  const keptAttrs: string[] = [];

  for (const a of rawAttrs) {
    const name = a.name;
    const lower = name.toLowerCase();

    // 3. Sensitive attribute names are always dropped regardless
    if (isSensitiveAttr(lower)) continue;

    // 1. For input: keep type, name, id, placeholder, aria-*, data-* (except data-snapshot-mask)
    if (tag === 'input') {
      if (lower === 'data-snapshot-mask') continue;
      const isAllowed =
        lower === 'type' ||
        lower === 'name' ||
        lower === 'id' ||
        lower === 'placeholder' ||
        lower.startsWith('aria-') ||
        lower.startsWith('data-');
      if (!isAllowed) continue;
    }

    // 1. For input, textarea, select, option: always drop the value attribute
    if (isForm && lower === 'value') continue;

    const rawVal = (a.value || '').slice(0, 120);

    if (masked) {
      const maskedVal = maskAttrValue(name, rawVal);
      keptAttrs.push(rawVal ? `${name}="${maskedVal}"` : name);
    } else {
      const escaped = escapeAttr(rawVal);
      keptAttrs.push(rawVal ? `${name}="${escaped}"` : name);
    }
  }

  // 1. For input: emit value="•••" only when element had a non-empty DOM value property
  if (tag === 'input') {
    const inputEl = el as HTMLInputElement;
    if (typeof inputEl.value === 'string' && inputEl.value !== '') {
      keptAttrs.push('value="•••"');
    }
  }

  const attrs = keptAttrs.join(' ');
  const open = attrs ? `<${tag} ${attrs}>` : `<${tag}>`;
  if (VOID_ELEMENTS.test(tag)) return attrs ? `<${tag} ${attrs}/>` : `<${tag}/>`;

  let text = '';
  if (!captureText) {
    const raw = (el.textContent || '').replace(/\s+/g, ' ').trim();
    text = raw ? '•••' : '';
  } else if (masked) {
    const raw = (el.textContent || '').replace(/\s+/g, ' ').trim();
    text = raw ? '•••' : '';
  } else if (tag === 'option') {
    text = '';
  } else if (tag === 'textarea') {
    const ta = el as HTMLTextAreaElement;
    const val = typeof ta.value === 'string' && ta.value !== '' ? ta.value : (el.textContent || '');
    text = val.trim() ? '•••' : '';
  } else if (tag === 'select') {
    const sel = el as HTMLSelectElement;
    const hasOpts = sel.options && sel.options.length > 0;
    const raw = (el.textContent || '').trim();
    text = (hasOpts || raw || sel.value) ? '•••' : '';
  } else {
    text = (el.textContent || '').replace(/\s+/g, ' ').trim().slice(0, 160);
  }

  return `${open}${text}</${tag}>`;
}

// Decide whether a matching rule's selector is worth keeping. We drop:
//  - universal / reset / normalize rules (`*`, `*,::before`, `html`, `:root`, pseudo-element resets),
//  - pure tag / tag-group rules (`button, input, select…`) — normalize noise, never the author's rule,
//  - single-class rules whose class is already in `element.classes` — on utility CSS (Tailwind) these
//    just echo `.mt-1 { margin-top: … }` for a class the reader already has, all cost and no signal.
// What survives is the genuinely informative stuff: compound/descendant author rules (`.hero h1`,
// `.step, .feature`, `.btn.primary`, `.mat-…-button-disabled`), id and attribute rules.
function skipSelector(sel: string, classSet: Set<string>): boolean {
  const s = sel.trim();
  if (s.includes('*')) return true;
  if (/::(before|after|backdrop|selection|placeholder|marker)/i.test(s)) return true;
  if (!/[.#[]/.test(s)) return true; // no class/id/attribute → tag-only reset
  // single class token (no combinator) that the element already lists → utility echo
  if (!/[ >+~,]/.test(s) && s.startsWith('.')) {
    const cls = s.slice(1).replace(/\\/g, '');
    if (classSet.has(cls)) return true;
  }
  return false;
}

// Collect the CSS rules that actually match `el`, RECURSING into grouped rules
// (@media / @layer / @supports / @container) — modern CSS (Tailwind's @layer, any
// responsive block) nests one level down, so a top-level-only scan returns nothing on
// exactly the frameworks people ship. Capped + filtered so the field stays cheap and signal-rich.
function collectAppliedRules(
  rules: CSSRuleList,
  el: Element,
  out: { selector: string; styles: string }[],
  cap: number,
  classSet: Set<string>,
): void {
  for (const rule of Array.from(rules)) {
    if (out.length >= cap) return;
    const styleRule = rule as CSSStyleRule;
    if (styleRule.selectorText) {
      const sel = styleRule.selectorText;
      if (skipSelector(sel, classSet)) continue;
      try {
        if (el.matches(sel)) {
          out.push({ selector: sel.slice(0, 160), styles: (styleRule.style.cssText || '').slice(0, 200) });
        }
      } catch (e) {}
    } else {
      const grouped = (rule as CSSGroupingRule).cssRules;
      if (grouped) collectAppliedRules(grouped, el, out, cap, classSet);
    }
  }
}

export type CaptureOptions = {
  captureText?: boolean;
};

// --- Metadata capture (ported from inject.js) ---------------------------
// `sourceAttr` is the configured DOM attribute carrying an element's source path.
export function captureMetadata(
  el: Element,
  sourceAttr: string,
  options?: CaptureOptions,
): Meta {
  const captureText = options?.captureText !== false;
  const selector = generateSelector(el);
  const snapshot = shallowSnapshot(el, captureText);
  const classes = (el.className && typeof el.className === 'string')
    ? el.className.split(/\s+/).filter(Boolean)
    : [];

  const computed: Record<string, string> = {};
  const applied: { selector: string; styles: string }[] = [];
  const cs = window.getComputedStyle(el);
  ['color', 'background-color', 'font-size', 'font-weight', 'margin', 'padding', 'border', 'text-align', 'display', 'flex-direction']
    .forEach((p) => {
      const v = cs.getPropertyValue(p);
      if (v) computed[p] = v.trim();
    });
  const inlineStyle = (el as HTMLElement).style;
  if (inlineStyle && inlineStyle.cssText) computed['inline-style'] = inlineStyle.cssText;

  const APPLIED_RULES_CAP = 6;
  const classSet = new Set(classes);
  for (const sheet of Array.from(document.styleSheets)) {
    if (applied.length >= APPLIED_RULES_CAP) break;
    let rules: CSSRuleList | undefined;
    try {
      rules = sheet.cssRules || (sheet as CSSStyleSheet).rules;
    } catch (e) {
      continue; // cross-origin
    }
    if (!rules) continue;
    collectAppliedRules(rules, el, applied, APPLIED_RULES_CAP, classSet);
  }

  let parent: Record<string, unknown> = {};
  if (el.parentElement) {
    const p = el.parentElement;
    parent = {
      tag: p.tagName.toLowerCase(),
      classes: (p.className && typeof p.className === 'string') ? p.className.split(/\s+/).filter(Boolean) : [],
      id: p.id || null,
    };
  }

  // Source path, tiered: (1) the configured attribute — respects apps that already invest in a
  // custom build plugin for this; (2) zero-config framework dev-mode internals (React/Vue) — see
  // framework-source.ts; (3) null, falling through to skill.md's own text/class/attribute search.
  let sourcePath: string | null = null;
  let node: Element | null = el;
  while (node && node.getAttribute) {
    const v = node.getAttribute(sourceAttr);
    if (v) {
      sourcePath = v;
      break;
    }
    node = node.parentElement;
  }
  if (!sourcePath) {
    sourcePath = detectFrameworkSourcePath(el);
  }

  return {
    // Internal display fields (not sent to API)
    _tag: el.tagName.toLowerCase(),
    _sourcePath: sourcePath,
    _snapshotPreview: snapshot,
    // API element shape (camelCase)
    selector,
    snapshot,
    classes: JSON.stringify(classes),
    computedStyles: JSON.stringify(computed),
    appliedCssRules: JSON.stringify(applied),
    sourcePath,
    parentInfo: JSON.stringify(parent),
  };
}
