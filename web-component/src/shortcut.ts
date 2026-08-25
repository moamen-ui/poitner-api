// Keyboard shortcut for "add a comment" — acts exactly like clicking the toolbar's inspect
// button. Stored server-side on the user's account (User.AddCommentShortcut via
// PATCH /api/me/preferences) so it follows the person across every browser/machine they log
// into — not a per-browser localStorage preference. element.ts caches the resolved value on
// `this.user`/`pointer_user` purely for fast bootstrap, exactly like displayName/roleName
// already are; the server is the source of truth.
//
// Default is Ctrl+Alt+Shift+C (Windows/Linux) / Control+Option+Shift+C (Mac) — stacking all
// three standard modifiers deliberately, since two-modifier combos on "C" are already spoken
// for: Ctrl+Shift+C / Cmd+Option+C is the browser's own "Inspect Element" DevTools shortcut
// (confirmed colliding in practice), and this widget's audience — people testing a page with
// DevTools open — is exactly who'd hit that. A triple-modifier chord is virtually never
// pre-bound by a browser, OS, or host page.

export interface ShortcutBinding {
  /**
   * KeyboardEvent.code — the PHYSICAL key position (e.g. "KeyC", "Digit1", "F5"), not
   * KeyboardEvent.key. This matters specifically because of Option/Alt on macOS: holding it
   * remaps what `key` reports for letter keys (Option+Shift+C can report "Ç", not "c"), which
   * silently broke matching for any combo involving Alt/Option. `code` is layout- and
   * modifier-independent, so it doesn't have this problem.
   */
  code: string;
  alt: boolean;
  shift: boolean;
  ctrl: boolean;
  meta: boolean;
}

export const DEFAULT_SHORTCUT: ShortcutBinding = { code: 'KeyC', alt: true, shift: true, ctrl: true, meta: false };

const MODIFIER_TOKENS = new Set(['ctrl', 'alt', 'shift', 'meta']);

/**
 * Parses the server's compact storage format — modifier tokens (any subset, any order, case
 * insensitive) joined by "+", always ending in the KeyboardEvent.code value, e.g.
 * "ctrl+alt+shift+KeyC". Falls back to the built-in default on missing/empty/unparseable input
 * (including a user profile that predates this feature, or the feature's earlier `key`-based
 * format from before this fix).
 */
export function parseShortcut(raw: string | null | undefined): ShortcutBinding {
  if (!raw) return { ...DEFAULT_SHORTCUT };
  const parts = raw.split('+').map((p) => p.trim()).filter(Boolean);
  const code = parts[parts.length - 1];
  if (!code || MODIFIER_TOKENS.has(code.toLowerCase())) return { ...DEFAULT_SHORTCUT };
  const mods = new Set(parts.slice(0, -1).map((p) => p.toLowerCase()));
  return {
    code,
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
  parts.push(binding.code);
  return parts.join('+');
}

export function matchesShortcut(e: KeyboardEvent, binding: ShortcutBinding): boolean {
  return (
    e.code === binding.code &&
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

// Turns a physical-key code into a short display label — "KeyC" -> "C", "Digit1" -> "1",
// "F5" stays "F5", etc. Falls back to the raw code for anything unrecognized.
function codeToLabel(code: string): string {
  if (code.startsWith('Key')) return code.slice(3);
  if (code.startsWith('Digit')) return code.slice(5);
  if (code === 'Space') return 'Space';
  if (code === 'Escape') return 'Esc';
  if (code === 'Enter') return 'Enter';
  return code;
}

/** Human-readable label, e.g. "Ctrl+Alt+Shift+C" or "⌃⌥⇧C" on Mac. */
export function formatShortcut(binding: ShortcutBinding, mac = isMacPlatform()): string {
  const label = codeToLabel(binding.code);
  const parts: string[] = [];
  if (mac) {
    if (binding.ctrl) parts.push('⌃');
    if (binding.alt) parts.push('⌥');
    if (binding.shift) parts.push('⇧');
    if (binding.meta) parts.push('⌘');
    parts.push(label);
    return parts.join('');
  }
  if (binding.ctrl) parts.push('Ctrl');
  if (binding.meta) parts.push('Win');
  if (binding.alt) parts.push('Alt');
  if (binding.shift) parts.push('Shift');
  parts.push(label);
  return parts.join('+');
}
