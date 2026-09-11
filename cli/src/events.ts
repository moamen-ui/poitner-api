import { fetchApi, ApiOptions } from './api.js';

export async function recordEvent(options: ApiOptions, type: string, source: string, projectKey?: string, meta?: any): Promise<void> {
  try {
    await fetchApi('/api/events', options, {
      method: 'POST',
      body: JSON.stringify({
        type,
        source,
        projectKey,
        meta
      })
    });
  } catch (err) {
    // Fire-and-forget, swallow errors
  }
}
