// Light/dark theme resolution for the widget's own shadow-DOM UI.
//
// Priority (see element.ts's resolveTheme): an explicit account preference (User.theme, set from
// this widget's own account menu OR the dashboard — same field, PATCH /api/me/preferences) always
// wins; failing that, the HOST PAGE's own rendered theme; failing that, the OS preference. The
// site-detection step is what lets a light-themed page keep the widget light even on a
// dark-OS visitor, and vice versa, without the visitor having to pick anything.

export type ThemeMode = 'light' | 'dark';

function relativeLuminance(r: number, g: number, b: number): number {
  const lin = (c: number) => {
    const s = c / 255;
    return s <= 0.03928 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4);
  };
  return 0.2126 * lin(r) + 0.7152 * lin(g) + 0.0722 * lin(b);
}

// Parses a computed `rgb()`/`rgba()` string. Returns null for anything else (e.g. a named color
// a browser never actually returns from getComputedStyle, or a parse failure) so the caller can
// keep walking rather than treat garbage as black.
function parseRgba(color: string): [number, number, number, number] | null {
  const m = color.match(/rgba?\(([^)]+)\)/i);
  if (!m) return null;
  const parts = m[1]!.split(',').map((s) => parseFloat(s.trim()));
  const [r, g, b] = parts;
  const a = parts[3];
  if (r === undefined || g === undefined || b === undefined) return null;
  if ([r, g, b].some((n) => Number.isNaN(n))) return null;
  return [r, g, b, a === undefined || Number.isNaN(a) ? 1 : a];
}

export function detectSystemTheme(): ThemeMode {
  try {
    return typeof window !== 'undefined' && window.matchMedia?.('(prefers-color-scheme: dark)').matches
      ? 'dark'
      : 'light';
  } catch {
    return 'light';
  }
}

// Walks from <body> up to <html> looking for the first non-transparent background color a
// browser actually painted — most pages set it on one of these two elements even when the visible
// background really lives on a full-bleed wrapper further down. Falls back to the OS preference
// when both are transparent, since a page that never sets an explicit background is itself just
// rendering the browser's own (OS-themed) canvas color.
export function detectSiteTheme(): ThemeMode {
  try {
    const candidates = [document.body, document.documentElement].filter(Boolean) as Element[];
    for (const el of candidates) {
      const rgba = parseRgba(getComputedStyle(el).backgroundColor);
      if (rgba && rgba[3] > 0) return relativeLuminance(rgba[0], rgba[1], rgba[2]) < 0.5 ? 'dark' : 'light';
    }
  } catch {
    // fall through to the OS preference
  }
  return detectSystemTheme();
}
