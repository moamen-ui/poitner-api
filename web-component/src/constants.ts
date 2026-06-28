import type { StatusStr } from './types';

export const HL_CLASS = 'pointer-feedback-hl';

// Environment string → int mapping (API contract: 1=Local, 2=Staging, 3=Production)
export const ENV_MAP: Record<string, number> = { local: 1, staging: 2, production: 3 };

// Status int → string mapping (API contract: 1=Open, 2=ReadyToApply, 3=Applied, 4=Archived)
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

// Status model maps to the filter chips All / Open / Pending / Completed / Archived.
export const STATUS_LABEL: Record<string, string> = {
  open: 'open',
  'pending-apply': 'pending',
  applied: 'completed',
  archived: 'archived',
};
export const FILTERS: { key: string; label: string }[] = [
  { key: 'all', label: 'All' },
  { key: 'open', label: 'Open' },
  { key: 'pending-apply', label: 'Pending' },
  { key: 'applied', label: 'Completed' },
  { key: 'archived', label: 'Archived' },
];

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
