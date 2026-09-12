// Settings helpers for the replace-all PUT /api/admin/settings endpoint.
// Always read-modify-write: a partial body silently clears other settings.
import { get, put } from './api.mjs';

function toUpdateRequest(settings) {
  return {
    scopedAdminSignupEnabled: settings.scopedAdminSignupEnabled ?? false,
    appBaseUrl: settings.appBaseUrl ?? '',
    emailEnabled: settings.emailEnabled ?? false,
    emailFromEmail: settings.emailFromEmail ?? '',
    emailFromName: settings.emailFromName ?? '',
    emailDailyCap: settings.emailDailyCap ?? 250,
    demoMaxActive: settings.demoMaxActive ?? 100,
    demoTtlHours: settings.demoTtlHours ?? 24,
    demoPerEmailPerDay: settings.demoPerEmailPerDay ?? 3,
    demoCommentCap: settings.demoCommentCap ?? 10,
    extensionStoreUrl: settings.extensionStoreUrl ?? '',
    extensionZipUrl: settings.extensionZipUrl ?? '',
  };
}

export async function captureSettings(token) {
  const current = await get('/api/admin/settings', { token });
  return current;
}

export async function applySettings(patch, token) {
  const current = await captureSettings(token);
  const updated = {
    ...toUpdateRequest(current),
    ...patch,
  };
  return put('/api/admin/settings', updated, { token });
}

export async function restoreSettings(original, token) {
  if (!original) return;
  const body = toUpdateRequest(original);
  return put('/api/admin/settings', body, { token });
}
