// Mailpit client for transactional email assertions (00-HARNESS §5).
export const MAILPIT_URL = process.env.E2E_MAILPIT_URL || 'http://localhost:8025';

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

export async function clear() {
  const res = await fetch(`${MAILPIT_URL}/api/v1/messages`, { method: 'DELETE' });
  if (!res.ok && res.status !== 404) {
    throw new Error(`Failed to clear mailpit mailbox: ${res.status}`);
  }
}

export async function awaitMessage({ to, subjectIncludes, timeoutMs = 10000, intervalMs = 250 }) {
  const start = Date.now();
  let lastListing = null;

  while (Date.now() - start < timeoutMs) {
    try {
      const searchUrl = `${MAILPIT_URL}/api/v1/search?query=to:${encodeURIComponent(to)}`;
      const res = await fetch(searchUrl);
      if (res.ok) {
        const data = await res.json();
        const messages = data.messages || [];
        lastListing = messages;

        // Filter and sort by Created descending to get the newest message
        let matching = messages;
        if (subjectIncludes) {
          matching = matching.filter((m) =>
            (m.Subject || m.subject || '').toLowerCase().includes(subjectIncludes.toLowerCase()),
          );
        }

        if (matching.length > 0) {
          matching.sort((a, b) => {
            const dateA = new Date(a.Created || a.created || 0).getTime();
            const dateB = new Date(b.Created || b.created || 0).getTime();
            return dateB - dateA;
          });

          const newest = matching[0];
          const id = newest.ID || newest.id;
          const msgRes = await fetch(`${MAILPIT_URL}/api/v1/message/${id}`);
          if (msgRes.ok) {
            const full = await msgRes.json();
            return {
              id,
              subject: full.Subject || full.subject || '',
              to: full.To || full.to || [],
              html: full.HTML || full.html || '',
              text: full.Text || full.text || '',
              headers: full.Headers || full.headers || {},
            };
          }
        }
      }
    } catch {
      // transient connection error, keep polling until timeout
    }

    await sleep(intervalMs);
  }

  throw new Error(
    `awaitMessage timed out after ${timeoutMs}ms waiting for mail to '${to}' (subjectIncludes: '${subjectIncludes || ''}'). Last listing: ${JSON.stringify(lastListing)}`,
  );
}

export async function assertNoMail({ to, withinMs = 3000 }) {
  const start = Date.now();
  while (Date.now() - start < withinMs) {
    try {
      const searchUrl = `${MAILPIT_URL}/api/v1/search?query=to:${encodeURIComponent(to)}`;
      const res = await fetch(searchUrl);
      if (res.ok) {
        const data = await res.json();
        const messages = data.messages || [];
        if (messages.length > 0) {
          throw new Error(
            `assertNoMail failed: found ${messages.length} message(s) for '${to}': ${JSON.stringify(messages)}`,
          );
        }
      }
    } catch (err) {
      if (err.message.startsWith('assertNoMail failed:')) throw err;
    }
    await sleep(250);
  }
}

export function extractLink(html, pathPrefix) {
  if (!html) throw new Error('extractLink: HTML body is empty');
  // Match href="...pathPrefix..." or href='...pathPrefix...'
  const escapedPrefix = pathPrefix.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const regex = new RegExp(`href=["']([^"']*${escapedPrefix}[^"']*)["']`, 'i');
  const match = html.match(regex);
  if (!match) {
    throw new Error(`extractLink: link matching '${pathPrefix}' not found in HTML`);
  }
  return match[1];
}
