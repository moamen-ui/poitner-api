import { SNAPDOM_URL, SHOT_MAX_WIDTH, SHOT_HIGHLIGHT } from './constants';
import { generateSelector } from './dom';
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
    s.src = SNAPDOM_URL;
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

// --- Metadata capture (ported from inject.js) ---------------------------
// `sourceAttr` is the configured DOM attribute carrying an element's source path.
export function captureMetadata(el: Element, sourceAttr: string): Meta {
  const selector = generateSelector(el);
  const snapshot = el.outerHTML.length > 2000 ? el.outerHTML.slice(0, 2000) : el.outerHTML;
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

  for (const sheet of Array.from(document.styleSheets)) {
    let rules: CSSRuleList | undefined;
    try {
      rules = sheet.cssRules || (sheet as CSSStyleSheet).rules;
    } catch (e) {
      continue; // cross-origin
    }
    if (!rules) continue;
    for (const rule of Array.from(rules)) {
      const styleRule = rule as CSSStyleRule;
      if (!styleRule.selectorText) continue;
      try {
        if (el.matches(styleRule.selectorText)) {
          applied.push({ selector: styleRule.selectorText, styles: styleRule.style.cssText });
        }
      } catch (e) {}
    }
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

  // Source path: nearest ancestor carrying the configured attribute.
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
