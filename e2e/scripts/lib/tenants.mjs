// Tenant and plan teardown helpers for E2E suites.
import { get, del } from './api.mjs';

export async function deleteTenantByEmail(email, token) {
  if (!email) return;
  try {
    const list = await get('/api/admin/tenants', { token });
    if (!Array.isArray(list)) return;
    const target = list.find((t) => t.email?.toLowerCase() === email.toLowerCase());
    if (target) {
      await del(`/api/admin/tenants/${target.id}`, { token });
    }
  } catch (err) {
    // Teardown should not mask scenario results if already gone
    if (err.status !== 404) {
      console.warn(`[tenants.mjs] Failed to delete tenant ${email}:`, err.message);
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
