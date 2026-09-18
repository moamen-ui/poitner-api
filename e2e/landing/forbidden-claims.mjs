// e2e/landing/forbidden-claims.mjs
// Derived from docs/roadmap/LANDING-PLAN.md (Part 1 — PRD) §7 "Blocked — do not claim until the item ships".
// When an item ships and unblocks a claim, its execution doc's ## Docs section removes the entry.

export const FORBIDDEN_CLAIMS = [
  {
    id: 'percentage',
    label: 'Specific percentage metric (e.g. 96%)',
    pattern: /\b\d+%/i,
    blockedOn: 'A re-measurement against the shipped path (§3.3)'
  },
  {
    id: 'turn_count',
    label: 'Specific turn count (e.g. 24 turns, 2 turns)',
    pattern: /\b\d+\s*turns?\b/i,
    blockedOn: 'A re-measurement against the shipped path (§3.3)'
  },
  {
    id: 'tokens_figure',
    label: 'Token figure with number (e.g. 551,817 tokens, 18,000 tokens)',
    pattern: /\b\d[\d,]*\s*tokens?\b/i,
    blockedOn: 'A re-measurement against the shipped path (§3.3)'
  },
  {
    id: 'npx_install',
    label: 'One command to install / npx ...',
    pattern: /\bnpx\s+/i,
    blockedOn: 'R1.2 CLI'
  },
  {
    id: 'mcp_framing',
    label: 'Works natively inside Claude Code / Cursor (MCP framing)',
    pattern: /\bMCP\b/,
    blockedOn: 'R2.2 MCP server'
  },
  {
    id: 'pr_link',
    label: 'Comments link to the PR',
    pattern: /\b(?:links?\s+to\s+the\s+PR|pull\s+request)\b/i,
    blockedOn: 'R2.9 apply --pr'
  },
  {
    id: 'cloud_apply',
    label: 'We apply it for you in the cloud',
    pattern: /\bcloud\s+apply\b/i,
    blockedOn: 'R2.43 cloud apply'
  },
  {
    id: 'magic_link_invite',
    label: 'Invite clients with one link, no password',
    pattern: /\b(?:one\s+link,\s*no\s+password|magic\s+links?)\b/i,
    blockedOn: 'R2.5 quick-access magic links'
  },
  {
    id: 'exact_line_production',
    label: 'Starts at the exact file/line (in production)',
    pattern: /\bstarts?\s+at\s+the\s+(?:exact\s+)?(?:file|line)\b/i,
    blockedOn: 'R3.1 Vite plugin + manifest'
  },
  {
    id: 'trusted_by',
    label: 'Trusted by N teams',
    pattern: /\btrusted\s+by\b/i,
    blockedOn: 'Measurement'
  },
  {
    id: 'testimonials',
    label: 'Customer testimonial / case study marker',
    pattern: /\b(?:testimonial|case\s+study)\b/i,
    blockedOn: 'Having a customer who agreed in writing'
  },
  {
    id: 'sla_uptime',
    label: 'Uptime / SLA figures',
    pattern: /\b(?:99\.\d+%|uptime\s+guarantee|SLA)\b/i,
    blockedOn: 'Measurement'
  }
];
