// Keyboard shortcut for "add a comment" — acts exactly like clicking the toolbar's inspect
// button. Stored server-side on the user's account (User.AddCommentShortcut via
// PATCH /api/me/preferences) so it follows the person across every browser/machine they log
// into — not a per-browser localStorage preference. element.ts caches the resolved value on
// `this.user`/`pointer_user` purely for fast bootstrap, exactly like displayName/roleName
// already are; the server is the source of truth.
//
// Default is Alt+Shift+C (Windows/Linux) / Option+Shift+C (Mac) — deliberately NOT
// Ctrl+Shift+C or Cmd+Option+C, since those are the browser's own "Inspect Element" DevTools
// shortcut on Windows/Linux and Mac respectively, and this widget's audience (people testing a
// page with DevTools open) is exactly who'd collide with that.

export interface ShortcutBinding {
  /** KeyboardEvent.key for the non-modifier key, lowercased for single characters. */
  key: string;
  alt: boolean;
  shift: boolean;
  ctrl: boolean;
  meta: boolean;
}

export const DEFAULT_SHORTCUT: ShortcutBinding = { key: 'c', alt: true, shift: true, ctrl: false, meta: false };

const MODIFIER_TOKENS = new Set(['ctrl', 'alt', 'shift', 'meta']);

/**
 * Parses the server's compact storage format — modifier tokens (any subset, any order) joined
 * by "+", always ending in the key, e.g. "alt+shift+c". Falls back to the built-in default on
 * missing/empty/unparseable input (including a user profile that predates this feature).
 */
export function parseShortcut(raw: string | null | undefined): ShortcutBinding {
  if (!raw) return { ...DEFAULT_SHORTCUT };
  const parts = raw.toLowerCase().split('+').map((p) => p.trim()).filter(Boolean);
  const key = parts[parts.length - 1];
  if (!key || MODIFIER_TOKENS.has(key)) return { ...DEFAULT_SHORTCUT };
  const mods = new Set(parts.slice(0, -1));
  return {
    key,
    ctrl: mods.has('ctrl'),
    alt: mods.has('alt'),
    shift: mods.has('shift'),
    meta: mods.has('meta'),
  };
}

/** Inverse of parseShortcut — the exact string sent to PATCH /api/me/preferences. */
export function serializeShortcut(binding: ShortcutBinding): string {
  const parts: string[] = [];
  if (binding.ctrl) parts.push('ctrl');
  if (binding.alt) parts.push('alt');
  if (binding.shift) parts.push('shift');
  if (binding.meta) parts.push('meta');
  parts.push(binding.key.toLowerCase());
  return parts.join('+');
}

export function matchesShortcut(e: KeyboardEvent, binding: ShortcutBinding): boolean {
  return (
    e.key.toLowerCase() === binding.key.toLowerCase() &&
    e.altKey === binding.alt &&
    e.shiftKey === binding.shift &&
    e.ctrlKey === binding.ctrl &&
    e.metaKey === binding.meta
  );
}

export function isMacPlatform(): boolean {
  if (typeof navigator === 'undefined') return false;
  return /Mac|iPhone|iPad|iPod/.test(navigator.platform || navigator.userAgent || '');
}

/** Human-readable label, e.g. "Alt+Shift+C" or "⌥⇧C" on Mac. */
export function formatShortcut(binding: ShortcutBinding, mac = isMacPlatform()): string {
  const parts: string[] = [];
  if (mac) {
    if (binding.ctrl) parts.push('⌃');
    if (binding.alt) parts.push('⌥');
    if (binding.shift) parts.push('⇧');
    if (binding.meta) parts.push('⌘');
    parts.push(binding.key.length === 1 ? binding.key.toUpperCase() : binding.key);
    return parts.join('');
  }
  if (binding.ctrl) parts.push('Ctrl');
  if (binding.meta) parts.push('Win');
  if (binding.alt) parts.push('Alt');
  if (binding.shift) parts.push('Shift');
  parts.push(binding.key.length === 1 ? binding.key.toUpperCase() : binding.key);
  return parts.join('+');
}
