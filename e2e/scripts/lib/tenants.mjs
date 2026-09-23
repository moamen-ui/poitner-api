// Tenant and plan teardown helpers for E2E suites.
import { get, del } from './api.mjs';

// F9 (DB-11a cross-review): DELETE /api/admin/tenants is keyed on {workspaceId:guid}, never the
// legacy per-row int `Id` (TenantResponse.Id — kept only so old dashboard code doesn't null-ref;
// the SAME int can legitimately repeat across rows now that one identity may administer several
// workspaces, and it was never a valid `:guid` route segment to begin with — passing it 404s the
// route match silently). Also: since one identity can now own several workspaces, a single email
// can legitimately match several rows in `/api/admin/tenants` — delete every one, not just the
// first match, or a test's second/third workspace leaks past teardown.
export async function deleteTenantByEmail(email, token) {
  if (!email) return;
  const list = await get('/api/admin/tenants', { token });
  if (!Array.isArray(list)) return;
  const targets = list.filter((t) => t.email?.toLowerCase() === email.toLowerCase());
  for (const target of targets) {
    try {
      await del(`/api/admin/tenants/${target.workspaceId}`, { token });
    } catch (err) {
      // Already gone (e.g. the scenario deleted it itself, or a previous call in this same loop
      // already removed it) is fine to tolerate. Anything else is a real teardown failure and
      // must surface, not be swallowed — a silently-broken delete is exactly how this route
      // mismatch went unnoticed after F9.
      if (err.status !== 404) throw err;
    }
  }
}

export async function deletePlanById(id, token) {
  if (!id) return;
  try {
    await del(`/api/admin/plans/${id}`, { token });
  } catch (err) {
    if (err.status !== 404) {
      console.warn(`[tenants.mjs] Failed to delete plan ${id}:`, err.message);
    }
  }
}

export async function deletePlanBySlug(slug, token) {
  if (!slug) return;
  try {
    const list = await get('/api/admin/plans', { token });
    if (!Array.isArray(list)) return;
    const target = list.find((p) => p.slug === slug);
    if (target) {
      await del(`/api/admin/plans/${target.id}`, { token });
    }
  } catch (err) {
    if (err.status !== 404) {
      console.warn(`[tenants.mjs] Failed to delete plan slug ${slug}:`, err.message);
    }
  }
}
