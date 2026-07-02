import type { StatusStr } from './types';

export const HL_CLASS = 'pointer-feedback-hl';

// Environment string → int mapping (API contract: 1=Local, 2=Staging, 3=Production)
export const ENV_MAP: Record<string, number> = { local: 1, staging: 2, production: 3 };
// Reverse: int → canonical name, for the in-widget environment switcher.
export const ENV_NAME: Record<number, string> = { 1: 'local', 2: 'staging', 3: 'production' };

// Status int → internal string mapping (API contract: 1=Open, 2=ReadyToApply, 3=Applied, 4=Archived).
// These keys are used as the internal `Comment.status` type and as filter chip keys — do not change.
export const STATUS_STR: Record<number, StatusStr> = {
  1: 'open',
  2: 'pending-apply',
  3: 'applied',
  4: 'archived',
};
export const STATUS_INT: Record<StatusStr, number> = {
  open: 1,
  'pending-apply': 2,
  applied: 3,
  archived: 4,
};

// Status model maps to CSS class names used on filter chips.
export const STATUS_LABEL: Record<string, string> = {
  open: 'open',
  'pending-apply': 'pending',
  applied: 'completed',
  archived: 'archived',
};

// ---------------------------------------------------------------------------
// Runtime status catalog — loaded from GET /api/statuses on element init.
// Falls back to STATUS_FALLBACK so the toolbar always renders even if the
// fetch fails or the server is unreachable.
// ---------------------------------------------------------------------------

export interface StatusItem {
  value: number;
  name: string;
  label: string;
  color: string;
  order: number;
}

// Built-in fallback so the toolbar still renders if the fetch fails.
export const STATUS_FALLBACK: StatusItem[] = [
  { value: 1, name: 'Open',         label: 'Open',      color: '#2563eb', order: 1 },
  { value: 2, name: 'ReadyToApply', label: 'Ready',     color: '#d97706', order: 2 },
  { value: 3, name: 'Applied',      label: 'Completed', color: '#16a34a', order: 3 },
  { value: 4, name: 'Archived',     label: 'Archived',  color: '#6b7280', order: 4 },
];

// API transport indirection. A host (e.g. the Pointer browser extension) can set
// window.__POINTER_FETCH__ to route every request through its background worker —
// which bypasses the page's connect-src CSP. Absent that, it's just global fetch.
export function pfFetch(url: string, opts?: RequestInit): Promise<Response> {
  const t = typeof window !== 'undefined' ? window.__POINTER_FETCH__ : undefined;
  return t ? t(url, opts) : fetch(url, opts);
}

// Module-level singleton — the widget assumes one <pointer-feedback> per page (consistent with STATUS_STR/STATUS_LABEL above), so the catalog is shared module state by design.
let _catalog: StatusItem[] = STATUS_FALLBACK;
export function getStatusCatalog(): StatusItem[] { return _catalog; }
export async function loadStatusCatalog(server: string): Promise<void> {
  try {
    const res = await pfFetch(`${server.replace(/\/$/, '')}/api/statuses`);
    if (!res.ok) return;
    const body = await res.json();
    const data: StatusItem[] = body?.data ?? body;
    if (Array.isArray(data) && data.length) _catalog = data.slice().sort((a, b) => a.order - b.order);
  } catch { /* keep fallback */ }
}

/**
 * Build the filter-chip list from the current catalog.
 * Each chip's `key` maps to the internal `StatusStr` value (via STATUS_STR)
 * so that filtering by `c.status === chip.key` continues to work unchanged.
 * The "All" chip is prepended and has no catalog entry.
 */
export function catalogToFilters(): { key: string; label: string; color: string }[] {
  const chips: { key: string; label: string; color: string }[] = [
    { key: 'all', label: 'All', color: '' },
  ];
  for (const item of _catalog) {
    const key = STATUS_STR[item.value];
    if (key) chips.push({ key, label: item.label, color: item.color });
  }
  return chips;
}

// Collapsed-launcher corners.
export const POSITIONS = ['top-start', 'top-end', 'bottom-start', 'bottom-end'] as const;
export type LauncherPosition = (typeof POSITIONS)[number];

// Screenshot capture tuning.
export const SHOT_MAX_WIDTH = 1280; // downscale cap for the exported screenshot
export const SHOT_HIGHLIGHT = '#2563eb';

// This bundle's own URL (resolved at load time — in the IIFE, document.currentScript
// is the <script> that loaded pointer.js). Used to resolve sibling assets.
export const SCRIPT_SRC: string =
  ((document.currentScript as HTMLScriptElement | null)?.src) || '';

// Sibling assets, resolved relative to this script so they work cross-origin.
export const CSS_URL = SCRIPT_SRC ? new URL('pointer.css', SCRIPT_SRC).href : 'pointer.css';
export const SNAPDOM_URL = SCRIPT_SRC
  ? new URL('vendor/snapdom.js', SCRIPT_SRC).href
  : 'vendor/snapdom.js';
